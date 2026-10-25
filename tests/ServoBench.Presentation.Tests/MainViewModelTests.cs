using System.Globalization;
using System.Net.Sockets;
using ServoBench.Core.Drive;
using ServoBench.Core.Profiles;
using Xunit;

namespace ServoBench.Presentation.Tests;

public class MainViewModelTests
{
    private readonly FakeDriveClientFactory _factory = new();
    private readonly FakeProfileFileService _files = new();
    private readonly MainViewModel _vm;

    public MainViewModelTests()
    {
        _vm = new MainViewModel(_factory, _files, culture: CultureInfo.InvariantCulture);
    }

    private FakeDriveClient Drive => _factory.Client;

    private ParameterViewModel Row(ParameterDefinition definition) =>
        _vm.Parameters.Single(p => p.Definition == definition);

    private async Task ConnectAsync()
    {
        await _vm.ConnectCommand.ExecuteAsync(null);
        Assert.True(_vm.IsConnected);
    }

    [Fact]
    public void Grid_shows_configuration_and_nameplate_but_not_the_live_setpoint()
    {
        Assert.DoesNotContain(ParameterCatalog.SpeedSetpoint, _vm.Parameters.Select(p => p.Definition));
        Assert.Equal(ParameterCatalog.All.Count - 1, _vm.Parameters.Count);
        Assert.False(_vm.ReadAllCommand.CanExecute(null));
    }

    [Fact]
    public async Task Connecting_loads_drive_values_and_enables_commands()
    {
        _vm.Host = "10.0.0.5";
        _vm.Port = 502;
        Drive.Values[ParameterCatalog.CurrentLimit.Key] = 4.5;

        await ConnectAsync();

        Assert.Equal(("10.0.0.5", 502), _factory.LastEndpoint);
        Assert.Equal("4.5", Row(ParameterCatalog.CurrentLimit).EditText);
        Assert.False(_vm.HasPendingChanges);
        Assert.True(_vm.ReadAllCommand.CanExecute(null));
        Assert.False(_vm.ConnectCommand.CanExecute(null));
        Assert.False(_vm.WriteChangesCommand.CanExecute(null));
    }

    [Fact]
    public async Task Failed_connection_is_reported_and_leaves_the_view_disconnected()
    {
        Drive.ConnectFailure = new SocketException((int)SocketError.ConnectionRefused);

        await _vm.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(ConnectionState.Disconnected, _vm.State);
        Assert.Contains("Could not connect", _vm.StatusMessage);
        Assert.True(Drive.IsDisposed);
        Assert.True(_vm.ConnectCommand.CanExecute(null));
    }

    [Fact]
    public async Task Invalid_edit_shows_an_error_and_blocks_writing()
    {
        await ConnectAsync();
        var row = Row(ParameterCatalog.CurrentLimit);

        row.EditText = "99";

        Assert.True(row.HasErrors);
        Assert.Contains("between", row.Error);
        Assert.True(_vm.HasInvalidEdits);
        Assert.False(_vm.WriteChangesCommand.CanExecute(null));
    }

    [Fact]
    public async Task Writing_sends_only_changed_rows_and_reads_back()
    {
        await ConnectAsync();
        Row(ParameterCatalog.CurrentLimit).EditText = "8.0";
        Row(ParameterCatalog.SpeedLoopKp).EditText = "1.25";
        Row(ParameterCatalog.MaxSpeed).EditText = "3000"; // unchanged from the drive
        Assert.Equal(2, _vm.PendingChangeCount);
        Assert.True(_vm.WriteChangesCommand.CanExecute(null));
        var readsBefore = Drive.SnapshotReads;

        await _vm.WriteChangesCommand.ExecuteAsync(null);

        Assert.Equal(
            new[] { ParameterCatalog.CurrentLimit, ParameterCatalog.SpeedLoopKp },
            Drive.Writes.Select(w => w.Parameter).ToArray());
        Assert.Equal(readsBefore + 1, Drive.SnapshotReads);
        Assert.False(_vm.HasPendingChanges);
        Assert.Equal("Wrote 2 parameters.", _vm.StatusMessage);
    }

    [Fact]
    public async Task A_rejected_value_is_flagged_on_its_row_and_the_rest_still_write()
    {
        await ConnectAsync();
        Drive.RejectedKeys.Add(ParameterCatalog.SpeedLoopKi.Key);
        Row(ParameterCatalog.SpeedLoopKi).EditText = "12.00";
        Row(ParameterCatalog.MaxSpeed).EditText = "2500";

        await _vm.WriteChangesCommand.ExecuteAsync(null);

        Assert.Equal(2500, Drive.Values[ParameterCatalog.MaxSpeed.Key]);
        Assert.True(Row(ParameterCatalog.SpeedLoopKi).HasErrors);
        Assert.Contains("rejected 1", _vm.StatusMessage);
    }

    [Fact]
    public async Task Polling_never_overwrites_a_pending_edit()
    {
        await ConnectAsync();
        var row = Row(ParameterCatalog.AccelerationTime);
        row.EditText = "750";

        Drive.Values[ParameterCatalog.AccelerationTime.Key] = 600; // changed on the drive by someone else
        await _vm.PollAsync();

        Assert.Equal("750", row.EditText);
        Assert.Equal(600, row.DeviceValue);
        Assert.True(row.IsDirty);
    }

    [Fact]
    public async Task Polling_follows_drive_changes_when_nothing_is_being_edited()
    {
        await ConnectAsync();

        Drive.Values[ParameterCatalog.AccelerationTime.Key] = 600;
        await _vm.PollAsync();

        Assert.Equal("600", Row(ParameterCatalog.AccelerationTime).EditText);
        Assert.False(_vm.HasPendingChanges);
    }

    [Fact]
    public async Task Polling_updates_telemetry_and_the_trend()
    {
        await ConnectAsync();
        Drive.Status = DriveStatus.Enabled | DriveStatus.AtSpeed;
        Drive.ActualSpeed = 1490;
        Drive.MotorCurrent = 2.1;
        Drive.Values[ParameterCatalog.SpeedSetpoint.Key] = 1500;

        await _vm.PollAsync();
        await _vm.PollAsync();

        Assert.True(_vm.IsEnabled);
        Assert.True(_vm.IsAtSpeed);
        Assert.Equal(1490, _vm.ActualSpeed);
        Assert.Equal(1500, _vm.SpeedSetpoint);
        Assert.Equal(3, _vm.Trend.Count); // one from connecting, two from polling
        Assert.Equal(1490, _vm.Trend.Snapshot()[^1].Speed);
    }

    [Fact]
    public async Task Losing_the_connection_while_polling_disconnects_cleanly()
    {
        await ConnectAsync();
        Drive.NextReadFailure = new IOException("connection reset");

        await _vm.PollAsync();

        Assert.Equal(ConnectionState.Disconnected, _vm.State);
        Assert.Contains("Connection lost", _vm.StatusMessage);
        Assert.True(Drive.IsDisposed);
        Assert.True(_vm.ConnectCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_new_fault_is_announced_and_only_reset_is_offered()
    {
        await ConnectAsync();
        Drive.Status = DriveStatus.Faulted;
        Drive.Fault = DriveFault.Overtemperature;

        await _vm.PollAsync();

        Assert.True(_vm.IsFaulted);
        Assert.Contains("Overtemperature", _vm.StatusMessage);
        Assert.True(_vm.ResetFaultCommand.CanExecute(null));
        Assert.False(_vm.EnableCommand.CanExecute(null));

        await _vm.ResetFaultCommand.ExecuteAsync(null);

        Assert.False(_vm.IsFaulted);
        Assert.Equal("No fault", _vm.FaultText);
    }

    [Fact]
    public async Task Speed_command_is_validated_before_it_can_be_sent()
    {
        await ConnectAsync();

        _vm.SpeedCommandText = "5000";
        Assert.NotNull(_vm.SpeedCommandError);
        Assert.False(_vm.SendSpeedCommand.CanExecute(null));

        _vm.SpeedCommandText = "-1200";
        Assert.Null(_vm.SpeedCommandError);
        await _vm.SendSpeedCommand.ExecuteAsync(null);

        Assert.Equal(-1200, Drive.Values[ParameterCatalog.SpeedSetpoint.Key]);
        Assert.Equal(-1200, _vm.SpeedSetpoint);
    }

    [Fact]
    public async Task Stop_sends_a_zero_setpoint()
    {
        await ConnectAsync();
        Drive.Values[ParameterCatalog.SpeedSetpoint.Key] = 2000;

        await _vm.StopCommand.ExecuteAsync(null);

        Assert.Equal(0, Drive.Values[ParameterCatalog.SpeedSetpoint.Key]);
        Assert.Equal("0", _vm.SpeedCommandText);
    }

    [Fact]
    public async Task Saving_a_profile_stores_drive_values_not_unsent_edits()
    {
        await ConnectAsync();
        Row(ParameterCatalog.CurrentLimit).EditText = "9.0"; // not written

        await _vm.SaveProfileCommand.ExecuteAsync(null);

        var saved = ProfileSerializer.Deserialize(_files.Files["C:/profiles/conveyor.json"]);
        Assert.Equal("conveyor", saved.Profile.Name);
        Assert.Equal(6.0, saved.ApplicableValues[ParameterCatalog.CurrentLimit]);
    }

    [Fact]
    public async Task Cancelling_the_save_dialog_writes_nothing()
    {
        await ConnectAsync();
        _files.SavePath = null;

        await _vm.SaveProfileCommand.ExecuteAsync(null);

        Assert.Empty(_files.Files);
    }

    [Fact]
    public async Task Loading_a_profile_stages_changes_without_writing_them()
    {
        await ConnectAsync();
        var values = ParameterCatalog.All.ToDictionary(p => p.Key, p => p.DefaultValue);
        values[ParameterCatalog.CurrentLimit.Key] = 3.5;
        values[ParameterCatalog.DecelerationTime.Key] = 250;
        _files.Files["C:/profiles/conveyor.json"] =
            ProfileSerializer.Serialize(ProfileSerializer.Create("conveyor", values, DateTimeOffset.UnixEpoch));

        await _vm.LoadProfileCommand.ExecuteAsync(null);

        Assert.Empty(Drive.Writes);
        Assert.Equal(2, _vm.PendingChangeCount);
        Assert.Equal("3.5", Row(ParameterCatalog.CurrentLimit).EditText);
        Assert.Contains("2 change(s) staged", _vm.StatusMessage);
    }

    [Fact]
    public async Task A_broken_profile_file_is_reported_not_thrown()
    {
        await ConnectAsync();
        _files.Files["C:/profiles/conveyor.json"] = "{ broken";

        await _vm.LoadProfileCommand.ExecuteAsync(null);

        Assert.Contains("Could not load profile", _vm.StatusMessage);
        Assert.False(_vm.HasPendingChanges);
    }

    [Fact]
    public async Task Revert_discards_all_edits()
    {
        await ConnectAsync();
        Row(ParameterCatalog.CurrentLimit).EditText = "9.0";
        Row(ParameterCatalog.SpeedLoopKp).EditText = "oops";

        _vm.RevertChangesCommand.Execute(null);

        Assert.False(_vm.HasPendingChanges);
        Assert.False(_vm.HasInvalidEdits);
        Assert.Equal("6.0", Row(ParameterCatalog.CurrentLimit).EditText);
    }
}
