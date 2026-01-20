using System;
using System.Text;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Config;
using Vintagestory.GameContent; 
using Newtonsoft.Json;

namespace TraderMapTooltip
{
    public class TraderNotesConfig {
        // Händler Vermögen
        public string ColorTraderFunds = "#deffa1"; 
        
        // Überschriften
        public string ColorSelling = "#40a746"; 
        // Geändert auf #deffa1
        public string ColorBuying = "#deffa1";  
        
        // Item Details
        public string ColorDemand = "#9d9d9d";    
        public string ColorItemName = "#e5e6de";  
        public string ColorItemStack = "#9d9d9d"; 
        public string ColorMoney = "#deebc7";     
    }

    public class CachedTradeItem {
        public string Name { get; set; }
        public int Stock { get; set; }     
        public int StackSize { get; set; } 
        public int Price { get; set; }
        public bool IsSoldOut { get; set; }
    }

    public class SavedTrader {
        public string Name { get; set; }
        public string TraderType { get; set; }
        public long EntityId { get; set; }
        public int Money { get; set; }
        public bool IsDiscovered { get; set; } = false;
        public double LastUpdatedTotalDays { get; set; }
        public double NextRefreshTotalDays { get; set; }
        
        public List<CachedTradeItem> Sells { get; set; } = new List<CachedTradeItem>();
        public List<CachedTradeItem> Wants { get; set; } = new List<CachedTradeItem>();
        
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
    }

    public class TraderMapMod : ModSystem {
        private ICoreClientAPI capi;
        public static Dictionary<long, SavedTrader> Cache = new Dictionary<long, SavedTrader>();
        public static TraderNotesConfig Config;
        public static long LatestLayerId = 0;
        private string savePath;
        private bool wasTraderInventoryOpen = false;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

        public override void StartClientSide(ICoreClientAPI api) {
            this.capi = api;
            this.savePath = Path.Combine(api.DataBasePath, "ModData", "tradernotes_cache.json");

            try {
                Config = api.LoadModConfig<TraderNotesConfig>("TraderNotesConfig.json") ?? new TraderNotesConfig();
                api.StoreModConfig(Config, "TraderNotesConfig.json");
            } catch { Config = new TraderNotesConfig(); }

            LoadCache();
            
            api.Event.RegisterCallback((dt) => {
                api.Event.EnqueueMainThreadTask(SetupMapLayer, "tradernotes_map_setup");
            }, 3000);
            
            api.Event.RegisterGameTickListener(OnClientTick, 500);
            api.Event.LeaveWorld += SaveCache;
        }

        private void SetupMapLayer() {
            if (capi?.World == null) return;
            var mapManager = capi.ModLoader.GetModSystem<WorldMapManager>();
            if (mapManager == null) return;
            if (!mapManager.MapLayers.Any(l => l is TraderMapLayer)) {
                LatestLayerId = DateTime.Now.Ticks;
                mapManager.MapLayers.Add(new TraderMapLayer(capi, mapManager, LatestLayerId));
            }
        }

        private string DetectTraderType(Entity entity) {
            string tag = (entity.WatchedAttributes.GetString("traderTag") ?? entity.Attributes.GetString("traderTag") ?? "").ToLower();
            string code = entity.Code?.Path?.ToLower() ?? "";
            string combined = (tag + " " + code).ToLower();

            if (combined.Contains("furniture")) return Lang.Get("tradernotes:type-furniture");
            if (combined.Contains("clothing")) return Lang.Get("tradernotes:type-clothing");
            if (combined.Contains("general") || combined.Contains("commodities")) return Lang.Get("tradernotes:type-general");
            if (combined.Contains("artisan")) return Lang.Get("tradernotes:type-artisan");
            if (combined.Contains("agriculture")) return Lang.Get("tradernotes:type-agriculture");
            if (combined.Contains("survival")) return Lang.Get("tradernotes:type-survival");
            if (combined.Contains("build")) return Lang.Get("tradernotes:type-building");
            if (combined.Contains("luxuries")) return Lang.Get("tradernotes:type-luxuries");
            if (combined.Contains("treasure")) return Lang.Get("tradernotes:type-treasure");

            return Lang.Get("tradernotes:type-unknown");
        }

        private void OnClientTick(float dt) {
            if (capi?.World?.Player == null) return;
            var currentTraderInv = capi.World.Player.InventoryManager.OpenedInventories.FirstOrDefault(i => i is InventoryTrader) as InventoryTrader;

            bool isCurrentlyOpen = currentTraderInv != null;
            if (isCurrentlyOpen || wasTraderInventoryOpen) {
                UpdateActiveTrader(currentTraderInv);
            }
            wasTraderInventoryOpen = isCurrentlyOpen;

            foreach (var entity in capi.World.LoadedEntities.Values) {
                if (entity?.Code?.Path != null && entity.Code.Path.Contains("trader")) {
                    if (Cache.TryGetValue(entity.EntityId, out var entry)) {
                        entry.X = entity.Pos.X; entry.Y = entity.Pos.Y; entry.Z = entity.Pos.Z;
                    }
                }
            }
        }

        private void UpdateActiveTrader(InventoryTrader traderInv) {
            EntityTradingHumanoid nearestTrader = capi.World.GetNearestEntity(capi.World.Player.Entity.Pos.XYZ, 10f, 10f, (e) => e is EntityTradingHumanoid) as EntityTradingHumanoid;
            if (nearestTrader == null) return;

            long id = nearestTrader.EntityId;
            if (!Cache.ContainsKey(id)) {
                Cache[id] = new SavedTrader {
                    Name = nearestTrader.GetBehavior<EntityBehaviorNameTag>()?.DisplayName ?? Lang.Get("tradernotes:trader-defaultname"),
                    EntityId = id,
                    TraderType = DetectTraderType(nearestTrader)
                };
            }

            var entry = Cache[id];
            bool changed = false;

            string currentType = DetectTraderType(nearestTrader);
            if (entry.TraderType != currentType) { entry.TraderType = currentType; changed = true; }

            int currentMoney = nearestTrader.Inventory?.MoneySlot?.Empty == false ? nearestTrader.Inventory.MoneySlot.StackSize : 0;
            if (entry.Money != currentMoney) { entry.Money = currentMoney; changed = true; }

            if (traderInv != null) {
                double now = capi.World.Calendar.TotalDays;
                double timeRemaining = nearestTrader.NextRefreshTotalDays();
                double targetDate = now + timeRemaining;

                if (Math.Abs(entry.NextRefreshTotalDays - targetDate) > 0.01) {
                    entry.NextRefreshTotalDays = targetDate;
                    changed = true;
                }
            }

            if (traderInv != null) {
                List<CachedTradeItem> sells = new List<CachedTradeItem>();
                List<CachedTradeItem> wants = new List<CachedTradeItem>();

                for (int i = 0; i < traderInv.Count; i++) {
                    var slot = traderInv[i];
                    if (slot?.Itemstack == null) continue;

                    if (slot.Itemstack.Collectible?.Code.Path.Contains("gear-rusty") == true) continue;

                    bool isSoldOut = slot.DrawUnavailable;
                    
                    int availableTrades = 0;
                    int price = 0;
                    int stackSize = slot.Itemstack.StackSize;

                    if (slot is ItemSlotTrade tradeSlot && tradeSlot.TradeItem != null) {
                        availableTrades = tradeSlot.TradeItem.Stock;
                        price = tradeSlot.TradeItem.Price;
                    } 
                    else {
                        ITreeAttribute tradeAttr = slot.Itemstack.Attributes?.GetTreeAttribute("tradeprops") ?? slot.Itemstack.Attributes?.GetTreeAttribute("trade");
                        if (tradeAttr != null) {
                            if (tradeAttr.HasAttribute("stock")) availableTrades = tradeAttr.GetInt("stock");
                            else if (tradeAttr.HasAttribute("supply")) availableTrades = tradeAttr.GetInt("supply");
                            else if (tradeAttr.HasAttribute("demand")) availableTrades = tradeAttr.GetInt("demand");
                            
                            price = tradeAttr.GetInt("price", 0);
                        } else {
                            availableTrades = stackSize;
                        }
                    }

                    string itemName = Lang.Get("tradernotes:item-unknown");
                    try {
                        ITreeAttribute tradeAttrName = slot.Itemstack.Attributes?.GetTreeAttribute("tradeprops");
                        var tradeStack = tradeAttrName?.GetItemstack("stack");
                        itemName = tradeStack?.GetName() ?? slot.Itemstack.GetName();
                    } catch { }

                    var itemInfo = new CachedTradeItem {
                        Name = itemName,
                        Stock = isSoldOut ? 0 : availableTrades,
                        StackSize = stackSize,
                        Price = price,
                        IsSoldOut = isSoldOut
                    };

                    if (i < 16) sells.Add(itemInfo); else wants.Add(itemInfo);
                }

                string currentSellsJson = JsonConvert.SerializeObject(sells);
                string savedSellsJson = JsonConvert.SerializeObject(entry.Sells);
                string currentWantsJson = JsonConvert.SerializeObject(wants);
                string savedWantsJson = JsonConvert.SerializeObject(entry.Wants);

                if (currentSellsJson != savedSellsJson || currentWantsJson != savedWantsJson) {
                    entry.Sells = sells; 
                    entry.Wants = wants;
                    entry.IsDiscovered = true;
                    entry.LastUpdatedTotalDays = capi.World.Calendar.TotalDays;
                    changed = true;
                }
            }

            if (changed) {
                entry.X = nearestTrader.Pos.X; entry.Y = nearestTrader.Pos.Y; entry.Z = nearestTrader.Pos.Z;
                SaveCache();
            }
        }

        private void LoadCache() {
            if (!File.Exists(savePath)) return;
            try { Cache = JsonConvert.DeserializeObject<Dictionary<long, SavedTrader>>(File.ReadAllText(savePath)) ?? new Dictionary<long, SavedTrader>(); } catch { }
        }

        public void SaveCache() {
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(savePath));
                File.WriteAllText(savePath, JsonConvert.SerializeObject(Cache, Formatting.Indented));
            } catch { }
        }
    }

    public class TraderMapLayer : MapLayer {
        private ICoreClientAPI capi;
        private long myId;

        public TraderMapLayer(ICoreClientAPI api, IWorldMapManager mapSink, long id) : base(api, mapSink) { 
            this.capi = api; 
            this.myId = id; 
        }
        
        public override string LayerGroupCode => "traders";
        public override string Title => Lang.Get("tradernotes:layer-title");
        public override EnumMapAppSide DataSide => EnumMapAppSide.Client;

        public override void Render(GuiElementMap map, float dt) {
            if (!Active || myId != TraderMapMod.LatestLayerId) return;

            foreach (var trader in TraderMapMod.Cache.Values) {
                Vec2f viewPos = new Vec2f();
                map.TranslateWorldPosToViewPos(new Vec3d(trader.X, trader.Y, trader.Z), ref viewPos);
                
                if (viewPos.X < 0 || viewPos.Y < 0 || viewPos.X > map.Bounds.OuterWidth || viewPos.Y > map.Bounds.OuterHeight) continue;
                
                capi.Render.RenderRectangle((float)(viewPos.X + map.Bounds.renderX - 3), (float)(viewPos.Y + map.Bounds.renderY - 3), 50f, 6f, 6f, ColorUtil.ColorFromRgba(255, 215, 0, 255));
            }
        }

        public override void OnMouseMoveClient(MouseEvent args, GuiElementMap map, StringBuilder hoverText) {
            if (!Active || myId != TraderMapMod.LatestLayerId) return;
            
            foreach (var trader in TraderMapMod.Cache.Values) {
                if (!trader.IsDiscovered) continue;
                Vec2f viewPos = new Vec2f();
                map.TranslateWorldPosToViewPos(new Vec3d(trader.X, trader.Y, trader.Z), ref viewPos);
                
                if (Math.Abs(viewPos.X - (args.X - map.Bounds.renderX)) < 12 && Math.Abs(viewPos.Y - (args.Y - map.Bounds.renderY)) < 12) {
                    var cfg = TraderMapMod.Config;
                    string currencyName = Lang.Get("tradernotes:currency");
                    string soldOutText = Lang.Get("tradernotes:soldout");
                    
                    // --- HEADER ---
                    hoverText.AppendLine($"<font color='#F5E6B5'><b>{trader.Name}</b></font>");
                    hoverText.AppendLine($"<font color='#BBBBBB'><i>{trader.TraderType}</i></font>");
                    hoverText.AppendLine($"<font color='{cfg.ColorTraderFunds}'>{Lang.Get("tradernotes:money-label")} {trader.Money}{currencyName}</font>");

                    // --- SELLS ---
                    if (trader.Sells.Count > 0) {
                        hoverText.AppendLine($"\n<font color='{cfg.ColorSelling}'>{Lang.Get("tradernotes:offers-label")}</font>");
                        foreach (var item in trader.Sells) {
                            BuildItemString(hoverText, item, cfg, currencyName, soldOutText);
                        }
                    }

                    // --- BUYS ---
                    if (trader.Wants.Count > 0) {
                        hoverText.AppendLine($"\n<font color='{cfg.ColorBuying}'>{Lang.Get("tradernotes:wants-label")}</font>");
                        foreach (var item in trader.Wants) {
                             BuildItemString(hoverText, item, cfg, currencyName, soldOutText);
                        }
                    }

                    double daysRemaining = trader.NextRefreshTotalDays - capi.World.Calendar.TotalDays;
                    
                    if (daysRemaining > 0.0) {
                        hoverText.AppendLine($"\n<font color='#AAAAAA'>{Lang.Get("tradernotes:refresh-in", daysRemaining.ToString("0.0"))}</font>");
                    } else {
                        hoverText.AppendLine($"\n<font color='#FF6666'><i>{Lang.Get("tradernotes:outdated")}</i></font>");
                    }

                    return;
                }
            }
        }

        private void BuildItemString(StringBuilder sb, CachedTradeItem item, TraderNotesConfig cfg, string currency, string soldOutTxt) {
            string soldOutMarker = item.IsSoldOut ? $" <font color='#FF6666'>[{soldOutTxt}]</font>" : "";

            // Format: 5x Itemname (64): 10 [Ausverkauft]
            sb.Append($" • <font color='{cfg.ColorDemand}'>{item.Stock}x</font> ");
            sb.Append($"<font color='{cfg.ColorItemName}'>{item.Name}</font> ");
            sb.Append($"<font color='{cfg.ColorItemStack}'>({item.StackSize})</font>: ");
            sb.Append($"<font color='{cfg.ColorMoney}'>{item.Price}{currency}</font>");
            
            sb.AppendLine(soldOutMarker);
        }
    }
}