using System;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;

namespace Minesweeper;

public class MinesweeperSettings : ISettings
{
    //Mandatory setting to allow enabling/disabling your plugin
    public ToggleNode Enable { get; set; } = new ToggleNode(false);

    public ToggleNode EnableDebugging { get; set; } = new ToggleNode(false);
    public RangeNode<int> DivineOrbPriceInChaos { get; set; } = new(0, 1000, 0);
    public RangeNode<int> MirrorOfKalandraPriceInChaos { get; set; } = new(0, 300000, 0);
    public RangeNode<int> UpdateCacheInterval { get; set; } = new(100, 1, 10000);

    public ToggleNode ShowItemPrices { get; set; } = new ToggleNode(true);
    public ToggleNode ShowLandmineBorders { get; set; } = new ToggleNode(true);
    public RangeNode<int> LandmineBorderThickness { get; set; } = new RangeNode<int>(1, 1, 10);
    
    public ToggleNode WarnBasedOnMedianPrice { get; set; } = new ToggleNode(true);
    [Menu("Warning %", "Warning threshold for median item price. Threshhold is % based, e.g. 50c -> 75c, 50% increase, will warn by default.")]
    public RangeNode<int> MedianPriceWarningThreshold { get; set; } = new RangeNode<int>(50, 0, 500);
    public ToggleNode WarnBasedOnTargetPrice { get; set; } = new ToggleNode(true);
    [Menu("Warning %", "Warning threshold for target item price. Threshhold is % based, e.g. 50c -> 75c, 50% increase, will warn by default.")]
    public RangeNode<int> TargetPriceWarningThreshold { get; set; } = new RangeNode<int>(50, 0, 500);

    [Menu(null, "Play the Minesweeper startup sound when opening the offline merchant window.")]
    public ToggleNode PlayStartupSound { get; set; } = new ToggleNode(true);
    public RangeNode<float> StartupSoundVolume { get; set; } = new(1, 0, 2);
    [Menu(null, "Play the Minesweeper landmine sound when a landmine is hovered.")]
    public ToggleNode PlayLandmineSound { get; set; } = new ToggleNode(true);
    public RangeNode<float> LandmineSoundVolume { get; set; } = new(1, 0, 2);

    //Put all your settings here if you can.
    //There's a bunch of ready-made setting nodes,
    //nested menu support and even custom callbacks are supported.
    //If you want to override DrawSettings instead, you better have a very good reason.
}