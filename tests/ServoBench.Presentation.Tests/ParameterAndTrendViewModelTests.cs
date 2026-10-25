using System.ComponentModel;
using System.Globalization;
using ServoBench.Core.Drive;
using Xunit;

namespace ServoBench.Presentation.Tests;

public class ParameterViewModelTests
{
    private static ParameterViewModel Create(IFormatProvider? culture = null) =>
        new(ParameterCatalog.CurrentLimit, culture ?? CultureInfo.InvariantCulture);

    [Fact]
    public void Errors_are_raised_through_INotifyDataErrorInfo_for_WPF_bindings()
    {
        var row = Create();
        row.UpdateFromDevice(6.0);
        var raised = new List<string?>();
        ((INotifyDataErrorInfo)row).ErrorsChanged += (_, e) => raised.Add(e.PropertyName);

        row.EditText = "abc";

        Assert.Equal(new string?[] { nameof(ParameterViewModel.EditText) }, raised);
        Assert.Single(row.GetErrors(nameof(ParameterViewModel.EditText)).Cast<string>());
        Assert.Empty(row.GetErrors(nameof(ParameterViewModel.Name)));
    }

    [Fact]
    public void Typing_the_same_value_in_a_different_form_is_not_a_change()
    {
        var row = Create();
        row.UpdateFromDevice(6.0);

        row.EditText = "6";

        Assert.False(row.IsDirty);
        Assert.False(row.HasErrors);
    }

    [Fact]
    public void Polling_does_not_rewrite_text_that_already_means_the_drive_value()
    {
        var row = Create();
        row.UpdateFromDevice(6.0);
        row.EditText = "6";

        row.UpdateFromDevice(6.0);

        Assert.Equal("6", row.EditText);
    }

    [Fact]
    public void Display_and_parsing_use_the_given_culture()
    {
        var row = Create(CultureInfo.GetCultureInfo("de-DE"));

        row.UpdateFromDevice(7.5);
        Assert.Equal("7,5", row.EditText);

        row.EditText = "8,5";
        Assert.Equal(8.5, row.PendingValue);
    }

    [Fact]
    public void Read_only_rows_are_never_dirty()
    {
        var row = new ParameterViewModel(ParameterCatalog.RatedSpeed, CultureInfo.InvariantCulture);
        row.UpdateFromDevice(3000);

        row.EditText = "1000";

        Assert.False(row.IsDirty);
        Assert.True(row.IsReadOnly);
    }
}

public class TrendViewModelTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Ring_buffer_keeps_the_newest_samples_in_order()
    {
        var trend = new TrendViewModel(capacity: 3);

        for (var i = 0; i < 5; i++)
        {
            trend.Add(T0.AddSeconds(i), setpoint: 0, speed: i, current: 0);
        }

        Assert.Equal(3, trend.Count);
        Assert.Equal(new[] { 2.0, 3.0, 4.0 }, trend.Snapshot().Select(s => s.Speed).ToArray());
        Assert.Equal(TimeSpan.FromSeconds(4), trend.Snapshot()[^1].Time);
    }

    [Fact]
    public void Frozen_trend_ignores_new_samples()
    {
        var trend = new TrendViewModel();
        trend.Add(T0, 0, 1, 0);

        trend.IsFrozen = true;
        trend.Add(T0.AddSeconds(1), 0, 2, 0);

        Assert.Equal(1, trend.Count);
    }

    [Fact]
    public void Clear_restarts_the_time_axis_and_notifies_the_chart()
    {
        var trend = new TrendViewModel();
        var updates = 0;
        trend.Updated += (_, _) => updates++;
        trend.Add(T0, 0, 1, 0);

        trend.ClearCommand.Execute(null);
        trend.Add(T0.AddMinutes(5), 0, 1, 0);

        Assert.Equal(TimeSpan.Zero, trend.Snapshot()[0].Time);
        Assert.Equal(3, updates);
    }
}
