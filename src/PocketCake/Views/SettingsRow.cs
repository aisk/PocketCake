using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PocketCake.Views;

/// <summary>A settings card: header and description on the left, the control on the right (styled in App.xaml).</summary>
public sealed partial class SettingsRow : ContentControl
{
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(SettingsRow), new PropertyMetadata(""));

    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(SettingsRow),
            new PropertyMetadata("", (d, _) => ((SettingsRow)d).UpdateParts()));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingsRow),
            new PropertyMetadata("", (d, _) => ((SettingsRow)d).UpdateParts()));

    private FrameworkElement? _descriptionText;
    private FrameworkElement? _icon;

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    /// <summary>A Segoe Fluent Icons glyph shown before the header; none if empty.</summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _descriptionText = GetTemplateChild("DescriptionText") as FrameworkElement;
        _icon = GetTemplateChild("Icon") as FrameworkElement;
        UpdateParts();
    }

    // An empty description would still take a line and push the header off-centre.
    private void UpdateParts()
    {
        if (_descriptionText is not null)
        {
            _descriptionText.Visibility = string.IsNullOrEmpty(Description) ? Visibility.Collapsed : Visibility.Visible;
        }

        if (_icon is not null)
        {
            _icon.Visibility = string.IsNullOrEmpty(Glyph) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
