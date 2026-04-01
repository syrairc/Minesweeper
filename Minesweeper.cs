using ExileCore;
using ExileCore.Shared.Enums;
using ExileCore.PoEMemory.MemoryObjects;
using SharpDX;
using Vector2 = System.Numerics.Vector2;
using System.Collections.Generic;
using System.Linq;
using System;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.Models;
using ItemFilterLibrary;
using System.IO;
using System.Reflection;
using System.Diagnostics;

namespace Minesweeper;

public partial class Minesweeper : BaseSettingsPlugin<MinesweeperSettings>
{
    // Helper: Debug logging
    private void DebugLog(string message, int logLevel = 5)
    {
        if (Settings.EnableDebugging)
            LogMessage(message, logLevel);
    }
    private class CachedItem
    {
        public float X { get; set; }
        public float Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int Price { get; set; }
        public string CurrencyType { get; set; }
        public ItemData ItemData { get; set; }
        public RectangleF ClientRect { get; set; }

        public int PriceInChaos { get; set; }
    }
    private readonly Dictionary<long, CachedItem> _itemCache = new();
    private int _medianPrice = 0;
    private int _lastMedianPrice;
    private int _targetPrice;
    private int _cacheUpdateCounter = 0;
    private bool _offlineMerchantOpen = false;
    private List<NormalInventoryItem> ItemList;
    private long _lastStartupSoundTimeUtcMs = 0;
    private long _lastLandmineSoundTimeUtcMs = 0;
    private long _lastStashAddress;
    private Element uiHover;

    internal const string StartUpSoundFile = "start.wav";
    internal const string LandmineSoundFile = "lose_minesweeper.wav";

    private void ClearCache()
    {
        _itemCache.Clear();
        _medianPrice = 0;
        DebugLog("Cache cleared.");
    }
    public override bool Initialise()
    {
        ClearCache();    
        GetExchangeRates();
        CopySoundFiles();
        return true;
    }
    
    private void CopySoundFiles()
    {
        string startUpSoundFile = Path.Join(ConfigDirectory, StartUpSoundFile);
        if (!File.Exists(startUpSoundFile))
        {
            using Stream fstream = Assembly.GetExecutingAssembly().GetManifestResourceStream(StartUpSoundFile);
            fstream.CopyTo(File.OpenWrite(startUpSoundFile));
        }

        string landmineSoundFile = Path.Join(ConfigDirectory, LandmineSoundFile);
        if (!File.Exists(landmineSoundFile))
        {
            using Stream fstream = Assembly.GetExecutingAssembly().GetManifestResourceStream(LandmineSoundFile);
            fstream.CopyTo(File.OpenWrite(landmineSoundFile));
        }
    }
    public void GetExchangeRates()
    {
        Settings.DivineOrbPriceInChaos.Value = (int)GameController.PluginBridge.GetMethod<Func<BaseItemType, double>>("NinjaPrice.GetBaseItemTypeValue")(
            GameController.Files.BaseItemTypes.Translate("Metadata/Items/Currency/CurrencyModValues"));

        Settings.MirrorOfKalandraPriceInChaos.Value = (int)GameController.PluginBridge.GetMethod<Func<BaseItemType, double>>("NinjaPrice.GetBaseItemTypeValue")(
            GameController.Files.BaseItemTypes.Translate("Metadata/Items/Currency/CurrencyDuplicate"));

    }
    public override Job Tick()
    {

        // Check if either OfflineMerchant or Purchase window is open
        Inventory merchantWindow = null;
        var offlineMerchant = GameController?.Game?.IngameState?.IngameUi?.OfflineMerchantPanel?.VisibleStash;
        var purchaseWindow = GameController?.Game?.IngameState?.IngameUi?.PurchaseWindow?.TabContainer?.VisibleStash;

        if (offlineMerchant != null && offlineMerchant.IsVisibleLocal)
            merchantWindow = offlineMerchant;
        else if (purchaseWindow != null && purchaseWindow.IsVisibleLocal)
            merchantWindow = purchaseWindow;

        if (merchantWindow == null)
        {
            if (_offlineMerchantOpen)
            {
                ClearCache();
                _offlineMerchantOpen = false;
            }
            return null;
        }

        if (!_offlineMerchantOpen) {
            GetExchangeRates();
            BuildCache(merchantWindow, true);
            GetTargetItemPrice(merchantWindow);
            PlayStartupSound();
            DebugLog("OfflineMerchant opened, refreshing exchange rates.");
        }

        _offlineMerchantOpen = true;
        
        BuildCache(merchantWindow);        
        GetHoveredItem();

        return null;
    }

    // TODO: Figure out if there is a way to find the target item based on the purple border.
    // Seems like the last item is always the target item if there is a target item; but there may not be a target item, in which case 
    // we don't want to set that as the target price...
    private void GetTargetItemPrice(Inventory stash)
    {
        var offlineMerchantItems = stash?.VisibleInventoryItems;
        if (offlineMerchantItems == null || offlineMerchantItems.Count == 0)        
            return;

        // Get the last child of the visibleinventoryitems, which is the target item (purple highlight)
        var targetItem = offlineMerchantItems.Last();
        if (targetItem == null)
            return;

        // Get the price from the target item's ItemData PublicPrice field
        var itemData = new ItemData(targetItem.Item, GameController);
        if (string.IsNullOrWhiteSpace(itemData.PublicPrice))        { 
            DebugLog("No PublicPrice found in target item.");
            return;
        }

        var parts = itemData.PublicPrice.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || parts[0] != "~b/o" || !int.TryParse(parts[1], out int price))
        {
            DebugLog($"PublicPrice format invalid for target item: {itemData.PublicPrice}");
            return;
        }
        string currency = string.Join(" ", parts.Skip(2));
        int priceInChaos = (int)(price * (currency.ToLower() switch
        {            
            "chaos" => 1,
            "divine" => Settings.DivineOrbPriceInChaos.Value,
            "mirror" => Settings.MirrorOfKalandraPriceInChaos.Value,
            _ => 0
        }));
        DebugLog($"Target item price: {price} {currency} ({priceInChaos} chaos)");
    }

    private void GetMedianPrice()
    {
        if (_itemCache.Count == 0)
        {
            _medianPrice = 0;
            DebugLog("No items in cache to calculate median price.");
            return;
        }
        
        var prices = _itemCache.Values
            .Select(i => i.PriceInChaos)
            .Where(p => p > 0)
            .OrderBy(p => p)
            .ToList();

        int mid = prices.Count / 2;
        _medianPrice = prices.Count % 2 == 1 ? prices[mid] : (int)Math.Round((prices[mid - 1] + prices[mid]) / 2.0);

        if (_medianPrice == _lastMedianPrice)
            return;

        DebugLog($"Median price changed: {_lastMedianPrice} -> {_medianPrice}");
        _lastMedianPrice = _medianPrice;
        
    }
    private void BuildCache(Inventory stash, bool forceUpdate = false)
    {
        _cacheUpdateCounter++;

        if (!forceUpdate && _cacheUpdateCounter < Settings.UpdateCacheInterval.Value && stash?.ServerInventory?.Address == _lastStashAddress)
            return;
        
        _cacheUpdateCounter = 0;
        _lastStashAddress = stash?.ServerInventory?.Address ?? 0;  
         
        _itemCache.Clear();

        // TODO: Get price from last visible inventory item; it is the price we are buying at
        var offlineMerchantItems = stash?.VisibleInventoryItems;
        if (offlineMerchantItems == null || offlineMerchantItems.Count == 0)        
            return;
        
        ItemList = offlineMerchantItems.ToList();
        if (ItemList == null || ItemList.Count == 0)            
            return;

        foreach (var entity in ItemList)
        {
            ItemData itemData = new ItemData(entity.Item, GameController);
            if (string.IsNullOrWhiteSpace(itemData.PublicPrice))
            {
                DebugLog("No PublicPrice found in ItemData.");
                continue;
            }

            var parts = itemData.PublicPrice.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // PublicPrice is in the format "~b/o <quantity> <currency>"
            if (parts.Length < 3 || parts[0] != "~b/o" || !int.TryParse(parts[1], out int price))
            {
                DebugLog($"PublicPrice format invalid: {itemData.PublicPrice}");
                continue;
            }

            string currency = string.Join(" ", parts.Skip(2));
            int w = itemData.Width;
            int h = itemData.Height;
            long key = entity.Item.Address;

            if (_itemCache.ContainsKey(key)) 
                continue;

            _itemCache[key] = new CachedItem
            {
                ClientRect = entity.GetClientRect(),
                Price = price,
                CurrencyType = currency,
                PriceInChaos = (int)(price * (currency.ToLower() switch
                {
                    "chaos" => 1,
                    "divine" => Settings.DivineOrbPriceInChaos.Value,
                    "mirror" => Settings.MirrorOfKalandraPriceInChaos.Value,
                    _ => 0
                })),
                ItemData = itemData
            };
            //DebugLog($"Cached item with InventoryId {key} [{w}x{h}] for {price} {currency}");
        }
        GetMedianPrice();
    }
    public override void Render()
    {
        // Render overlays for each cached item
        foreach (var cached in _itemCache)
        {
            var item = cached.Value;
            bool inPlayerShop = GameController.Game.IngameState?.IngameUi?.OfflineMerchantPanel?.IsVisibleLocal == true && !Settings.EnableDebugging;
        
            // Determine color based on price vs target price or median price
            Color color = Color.White;

            // Color gradient: green (cheaper) -> white (fair) -> red (more expensive)
            if (inPlayerShop)
            {
                color = Color.White;
            } else
            {          
                float? basePrice = null;
                if (Settings.WarnBasedOnTargetPrice && _targetPrice > 0)
                    basePrice = _targetPrice;
                else if (Settings.WarnBasedOnMedianPrice && _medianPrice > 0)
                    basePrice = _medianPrice;

                if (basePrice.HasValue && basePrice.Value > 0)
                {
                    float t = (item.PriceInChaos - basePrice.Value) / basePrice.Value;
                    t = Math.Clamp(t, -1f, 1f);
                    color = t < 0
                        ? Color.Lerp(Color.Green, Color.White, 1 + t)
                        : Color.Lerp(Color.White, Color.Red, t);
                }
            }

            // Use item.ClientRect for position and size
            var rect = item.ClientRect;
            var itemPos = rect.TopLeft;
            var itemSize = new Vector2(rect.Width, rect.Height);

            // Skip rendering if uiHover is an InventoryItem and its tooltip overlaps this item's rect
            if (GameController.Game.IngameState.UIHover != null)
            {
                var tooltip = GameController.Game.IngameState.UIHover.Tooltip;
                if (tooltip != null && tooltip.IsVisibleLocal)
                {
                    var tooltipRect = tooltip.GetClientRect();
                    // Inflate both rectangles by -3px to allow 3px overlap
                    var inflatedRect = new RectangleF(rect.X + 3, rect.Y + 3, rect.Width - 6, rect.Height - 6);
                    var inflatedTooltipRect = new RectangleF(tooltipRect.X + 3, tooltipRect.Y + 3, tooltipRect.Width - 6, tooltipRect.Height - 6);
                    if (inflatedRect.Intersects(inflatedTooltipRect)) 
                        continue;
                    
                }
            }

            if (Settings.ShowItemPrices)
            {
                // Draw price text in the bottom left inside the rectangle
                string currency = string.IsNullOrEmpty(item.CurrencyType) ? "" : char.ToLower(item.CurrencyType[0]).ToString();
                var text = $"{item.Price}{currency}";
                var textSize = Graphics.MeasureText(text);
                var textPos = new Vector2(itemPos.X + 2, itemPos.Y + itemSize.Y - textSize.Y - 2);
                Graphics.DrawTextWithBackground(text, textPos, color, Color.Black);
            }            

            if (inPlayerShop || (!Settings.WarnBasedOnMedianPrice && !Settings.WarnBasedOnTargetPrice) || !Settings.ShowLandmineBorders)
                continue;

            // Draw border if price exceeds median or target price thresholds (if enabled)
            bool drawBorder = false;
            Color borderColor = color;

            // Target price threshold
            if (Settings.WarnBasedOnTargetPrice && _targetPrice > 0)
            {
                float threshold = Settings.TargetPriceWarningThreshold.Value / 100f;
                if (item.PriceInChaos > _targetPrice * (1 + threshold))                
                    drawBorder = true;
            }
            // Median price threshold
            else if (Settings.WarnBasedOnMedianPrice && _medianPrice > 0)
            {
                float threshold = Settings.MedianPriceWarningThreshold.Value / 100f;
                if (item.PriceInChaos > _medianPrice * (1 + threshold))                
                    drawBorder = true;
            }

            if (drawBorder)
            {
                var borderRect = new RectangleF(itemPos.X - 1, itemPos.Y - 1, itemSize.X + 2, itemSize.Y + 2);
                Graphics.DrawFrame(borderRect, borderColor, Settings.LandmineBorderThickness.Value);
            }
        }

    }

    private void GetHoveredItem()
        {
        if (GameController.Game.IngameState?.IngameUi?.OfflineMerchantPanel?.IsVisibleLocal == true && !Settings.EnableDebugging)
            return;

        try
        {
            var _newHover = GameController?.Game?.IngameState?.UIHover;
            Inventory merchantWindow = null;
            var offlineMerchant = GameController?.Game?.IngameState?.IngameUi?.OfflineMerchantPanel?.VisibleStash;
            var purchaseWindow = GameController?.Game?.IngameState?.IngameUi?.PurchaseWindow?.TabContainer?.VisibleStash;

            if (offlineMerchant != null && offlineMerchant.IsVisibleLocal)
                merchantWindow = offlineMerchant;
            else if (purchaseWindow != null && purchaseWindow.IsVisibleLocal)
                merchantWindow = purchaseWindow;

            bool isInOfflineMerchant = merchantWindow != null && merchantWindow.IsVisibleLocal && _newHover.GetParentChain().Any(e => e.Address == merchantWindow.Address);

            if (_newHover is { } &&
                isInOfflineMerchant &&
                _newHover.AsObject<HoverItemIcon>()?.Item != null &&
                _newHover.AsObject<HoverItemIcon>()?.ToolTipType != ToolTipType.ItemInChat)
            {
                var newHoverIcon = _newHover.AsObject<HoverItemIcon>();
                bool isDifferentItem = true;
                if (uiHover is HoverItemIcon oldHoverIcon && oldHoverIcon.Item != null)
                {
                    isDifferentItem = newHoverIcon.Item.Address != oldHoverIcon.Item.Address;
                }
                if (isDifferentItem)
                {
                    uiHover = newHoverIcon;
                    IsItALandmine(uiHover);
                }
            }
            else
            {
                if (uiHover != null)                
                    uiHover = null;
            }
        }
        catch (Exception ex)
        {
             DebugLog($"Failed to get the hovered item: {ex}");
        }
    }

    private void IsItALandmine(Element hover)
    {
        if (GameController.Game.IngameState?.IngameUi?.OfflineMerchantPanel?.IsVisibleLocal == true && !Settings.EnableDebugging)
            return;

        if (!Settings.ShowLandmineBorders && !Settings.PlayLandmineSound)
            return;

        if (hover is HoverItemIcon hoverIcon && hoverIcon.Item != null)
        {
            long itemKey = hoverIcon.Item.Address;
            if (_itemCache.TryGetValue(itemKey, out var cachedItem))
            {
                // Median price check
                if (Settings.WarnBasedOnMedianPrice && _medianPrice > 0)
                {
                    float threshold = Settings.MedianPriceWarningThreshold.Value / 100f;
                    if (cachedItem.PriceInChaos > _medianPrice * (1 + threshold))
                    {
                        PlayLandmineSound();
                        DebugLog($"Landmine detected by median! PriceInChaos: {cachedItem.PriceInChaos}, Median: {_medianPrice}, Threshold: {Settings.MedianPriceWarningThreshold.Value}%");
                    }
                }

                // Target price check
                if (Settings.WarnBasedOnTargetPrice && _targetPrice > 0)
                {
                    float threshold = Settings.TargetPriceWarningThreshold.Value / 100f;
                    if (cachedItem.PriceInChaos > _targetPrice * (1 + threshold))
                    {
                        PlayLandmineSound();
                        DebugLog($"Landmine detected by target! PriceInChaos: {cachedItem.PriceInChaos}, Target: {_targetPrice}, Threshold: {Settings.TargetPriceWarningThreshold.Value}%");
                    }
                }
            }
        }
    }

    private void PlayStartupSound()
    {
        long now = Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
        if (!Settings.PlayStartupSound || now - _lastStartupSoundTimeUtcMs < 5000)
            return;

        string startupSoundFile = Path.Join(ConfigDirectory, "start.wav");
        if (File.Exists(startupSoundFile))
            GameController.SoundController.PlaySound(startupSoundFile, Settings.StartupSoundVolume.Value);

        _lastStartupSoundTimeUtcMs = now;
    }
    private void PlayLandmineSound()
    {
        long now = Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
        if (!Settings.PlayLandmineSound || now - _lastLandmineSoundTimeUtcMs < 2000)
            return;

        string landmineSoundFile = Path.Join(ConfigDirectory, "lose_minesweeper.wav");
        if (File.Exists(landmineSoundFile))            
            GameController.SoundController.PlaySound(landmineSoundFile, Settings.LandmineSoundVolume.Value);

        _lastLandmineSoundTimeUtcMs = now;
    }
    

}