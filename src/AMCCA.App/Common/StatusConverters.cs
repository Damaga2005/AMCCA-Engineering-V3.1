using System;
using System.Globalization;
using System.Windows.Data;

namespace AMCCA.App.Common;

/// <summary>SPEC/60 obligation 1: labels the global kill switch control with its current state and the
/// action clicking it will take, rather than a bare on/off indicator.</summary>
public class KillSwitchLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "Kill Switch: ENGAGED (click to clear)" : "Kill Switch: OFF (click to engage)";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>SPEC/60 obligation 2: renders a boolean system flag (e.g. publishing_enabled) as text
/// instead of leaving it to whatever a bare bool happens to stringify to.</summary>
public class EnabledDisabledConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "Enabled" : "Disabled";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
