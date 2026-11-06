namespace CrystalStudio;

public sealed class GeneratorValueEditor : NumericUpDown
{
    public static readonly StyledProperty<Slider?> SliderProperty = AvaloniaProperty.Register<GeneratorValueEditor, Slider?>(nameof(Slider));
    public static readonly StyledProperty<decimal> MultiplierProperty = AvaloniaProperty.Register<GeneratorValueEditor, decimal>(nameof(Multiplier), 1);
    private bool synchronizing;
    protected override Type StyleKeyOverride => typeof(NumericUpDown);
    public Slider? Slider { get => GetValue(SliderProperty); set => SetValue(SliderProperty, value); }
    public decimal Multiplier { get => GetValue(MultiplierProperty); set => SetValue(MultiplierProperty, value); }

    public GeneratorValueEditor()
    {
        ShowButtonSpinner = false; ClipValueToMinMax = true; FormatString = "0.##";
        Padding = new Thickness(6, 4); MinWidth = 60; TextAlignment = TextAlignment.Right;
        VerticalAlignment = VerticalAlignment.Center;
        ValueChanged += (_, _) =>
        {
            if (synchronizing || Slider is not { } slider || Value is not { } value || Multiplier <= 0) return;
            double number = Math.Clamp((double)(value / Multiplier), slider.Minimum, slider.Maximum);
            if (slider.IsSnapToTickEnabled)
            {
                if (slider.Ticks is { Count: > 0 })
                    number = slider.Ticks.Where(tick => tick >= slider.Minimum && tick <= slider.Maximum).OrderBy(tick => Math.Abs(tick - number)).FirstOrDefault(number);
                else if (slider.TickFrequency > 0)
                    number = slider.Minimum + Math.Round((number - slider.Minimum) / slider.TickFrequency) * slider.TickFrequency;
            }
            slider.Value = Math.Clamp(number, slider.Minimum, slider.Maximum);
            Synchronize();
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SliderProperty)
        {
            if (change.OldValue is Slider previous) previous.PropertyChanged -= SliderChanged;
            if (change.NewValue is Slider slider) slider.PropertyChanged += SliderChanged;
            Synchronize();
        }
        else if (change.Property == MultiplierProperty) Synchronize();
    }

    private void SliderChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == RangeBase.ValueProperty || change.Property == RangeBase.MinimumProperty || change.Property == RangeBase.MaximumProperty ||
            change.Property == IsEnabledProperty || change.Property == Slider.SmallChangeProperty || change.Property == Slider.TickFrequencyProperty ||
            change.Property == Slider.IsSnapToTickEnabledProperty || change.Property == Slider.TicksProperty) Synchronize();
    }

    private void Synchronize()
    {
        if (Slider is not { } slider || Multiplier <= 0 || synchronizing) return;
        synchronizing = true;
        try
        {
            if (Name == null) Name = slider.Name + "Value";
            Minimum = (decimal)slider.Minimum * Multiplier; Maximum = (decimal)slider.Maximum * Multiplier;
            Increment = (decimal)(slider.IsSnapToTickEnabled && slider.TickFrequency > 0 ? slider.TickFrequency : slider.SmallChange) * Multiplier;
            Value = (decimal)slider.Value * Multiplier; IsEnabled = slider.IsEnabled;
            Avalonia.Automation.AutomationProperties.SetName(this, Avalonia.Automation.AutomationProperties.GetName(slider));
            ToolTip.SetTip(this, LocalText.T("Enter an exact value; the slider's range and steps still apply."));
        }
        finally { synchronizing = false; }
    }
}
