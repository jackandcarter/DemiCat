namespace DemiCatPlugin;

using System;
using System.Numerics;
using Dalamud.Interface.Textures;

public sealed class DockItem
{
    public DockItem(
        string id,
        ISharedImmediateTexture? icon,
        Vector4 iconTint,
        string tooltip,
        Func<bool> isVisible,
        Func<bool> isEnabled,
        Func<bool> getIsOpen,
        Action<bool> setIsOpen,
        Action drawWindow)
    {
        Id = id;
        Icon = icon;
        IconTint = iconTint;
        Tooltip = tooltip;
        IsVisible = isVisible;
        IsEnabled = isEnabled;
        GetIsOpen = getIsOpen;
        SetIsOpen = setIsOpen;
        DrawWindow = drawWindow;
    }

    public string Id { get; }
    public ISharedImmediateTexture? Icon { get; }
    public Vector4 IconTint { get; }
    public string Tooltip { get; }
    public Func<bool> IsVisible { get; }
    public Func<bool> IsEnabled { get; }
    public Func<bool> GetIsOpen { get; }
    public Action<bool> SetIsOpen { get; }
    public Action DrawWindow { get; }
}
