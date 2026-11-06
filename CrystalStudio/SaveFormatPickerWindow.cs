using CrystalStudio.Formats;

namespace CrystalStudio;

/// <summary>
/// 另存为时选择目标格式。默认保存不弹这个窗口，直接使用设置里的默认保存格式。
/// </summary>
public sealed class SaveFormatPickerWindow : EditorWindow
{
    /// <summary>用户选中的格式；取消时为 null。</summary>
    public MapSaveFormat? Selected { get; private set; }

    public SaveFormatPickerWindow(MapSaveFormat current, string? suggestedName, Action<MapSaveFormat> confirmed)
    {
        Title = "另存为 · 选择格式";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new(24) };
        Content = panel;

        panel.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(suggestedName) ? "选择要写入的地图格式" : $"把 {suggestedName} 保存为：",
            FontSize = 19,
            TextWrapping = TextWrapping.Wrap,
            Margin = new(0, 0, 0, 14),
        });

        var options = new (MapSaveFormat Format, string Title, string Hint)[]
        {
            (MapSaveFormat.FollowSource, "跟随源格式（推荐）", "按打开时的布局写回：12 字节地图存 12 字节，14 字节存 14 字节。"),
            (MapSaveFormat.Crystal, "Crystal 地图（编辑器原生）", "保留全部图层与扩展数据，后续可无损再编辑。"),
            (MapSaveFormat.Mir2OldMap, "Mir2 经典地图 · 12 字节单元", "52 字节头 + BkImg/MidImg/FrImg，Legend of mir 结构的服务端通用格式。"),
            (MapSaveFormat.Mir2NewMap, "Mir2 新式地图 · 14 字节单元", "多出 BkIndex/MidIndex 两个库编号字段。"),
            (MapSaveFormat.Mir2EnhancedMap, "Mir2 增强地图 · 15 字节单元", "54 字节头，图像字与 0xAA38 异或，Map 2010 Ver 1.0。"),
        };

        var radios = new List<RadioButton>();
        foreach (var option in options)
        {
            var radio = new RadioButton
            {
                Name = "SaveFormat" + option.Format,
                GroupName = "MapSaveFormat",
                Content = option.Title,
                IsChecked = option.Format == current,
                Margin = new(0, 8, 0, 0),
            };
            var hint = new TextBlock
            {
                Text = option.Hint,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7,
                Margin = new(26, 1, 0, 4),
            };
            panel.Children.Add(radio);
            panel.Children.Add(hint);
            radios.Add(radio);
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked == true) Selected = option.Format;
            };
        }
        Selected = current;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new(0, 18, 0, 0),
        };
        var cancel = new Button { Content = LocalText.T("Cancel"), IsCancel = true };
        var ok = new Button
        {
            Theme = (Style)this.FindResource("PrimaryButton"),
            Content = "选择保存位置",
            IsDefault = true,
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);

        cancel.Click += (_, _) => { Selected = null; Close(false); };
        ok.Click += (_, _) => { Close(true); confirmed(Selected ?? current); };
    }
}
