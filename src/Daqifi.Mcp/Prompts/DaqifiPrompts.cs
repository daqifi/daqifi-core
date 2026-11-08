using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Daqifi.Mcp.Prompts;

/// <summary>
/// The measurements people actually ask a DAQ for, as MCP prompts.
/// </summary>
/// <remarks>
/// <para>
/// Each one returns the request a user would have typed, with the tool order spelled out. The
/// tool descriptions already say what each tool does in isolation; what they cannot say is which
/// four to call, in what order, to answer "is this signal noisy?". A client lists these as
/// ready-made starting points, so the common jobs do not have to be assembled from scratch —
/// correctly — by every user on their first session.
/// </para>
/// <para>
/// They are deliberately parameterized thinly: the channel and the duration, which is what
/// differs between two runs of the same job. Anything else the agent works out from the device,
/// as it would have anyway.
/// </para>
/// </remarks>
[McpServerPromptType]
public static class DaqifiPrompts
{
    [McpServerPrompt(Name = "check_for_noise", Title = "Check a signal for noise")]
    [Description("Capture a block of live data from a DAQiFi device and report whether any channel looks noisy.")]
    public static string CheckForNoise(
        [Description("Analog channels to look at, e.g. '0-3' or '0,2,5'. Defaults to 0-3.")] string channels = "0-3",
        [Description("How long to capture for, in milliseconds (default 2000).")] int durationMs = 2000)
        => $"""
            Capture {durationMs} ms off analog channels {channels} on my DAQiFi and tell me if anything looks noisy.

            Discover the device and connect to it first, then enable exactly those analog channels, set a
            sample rate that comfortably covers what you expect to see, and capture. Report per channel:
            the mean, the peak-to-peak, and anything periodic you can identify (mains hum at 50/60 Hz is
            the usual suspect). Compare measuredRateHz and deviceClockRateHz with the rate you asked for
            before you trust a frequency you read out of the data.
            """;

    [McpServerPrompt(Name = "start_sd_log", Title = "Log to the SD card")]
    [Description("Discover a DAQiFi device, configure channels and rate, and start on-device SD-card logging.")]
    public static string StartSdLog(
        [Description("Analog channels to log, e.g. '0-3' or '0,2,5'. Defaults to 0-3.")] string channels = "0-3",
        [Description("Sample rate in Hz (default 1000).")] int rateHz = 1000)
        => $"""
            Discover my DAQiFi, connect, enable analog channels {channels} at {rateHz} Hz, and start logging to the SD card.

            SD logging needs the USB/serial connection, not WiFi — the card and the radio share a bus — so
            connect over USB. Check the card has room before you start, and tell me the log file name the
            device is writing to, because I will need it to download the log later.
            """;

    [McpServerPrompt(Name = "read_voltage_now", Title = "Read a voltage now")]
    [Description("Read the voltage currently on one analog input of a DAQiFi device.")]
    public static string ReadVoltageNow(
        [Description("The analog input channel number (default 0).")] int channel = 0)
        => $"""
            What voltage is on analog channel {channel} right now?

            Discover and connect, enable that one channel, and read its latest value. A channel that comes
            back null is one that went silent — say that, rather than reporting it as 0 V. Leave the device
            as you found it when you are done.
            """;

    [McpServerPrompt(Name = "drive_and_measure", Title = "Drive an output and measure the response")]
    [Description("Drive a digital output on a DAQiFi device, then read an analog input to see what happened.")]
    public static string DriveAndMeasure(
        [Description("The digital channel to drive (default 3).")] int digitalChannel = 3,
        [Description("The analog channel to measure (default 0).")] int analogChannel = 0,
        [Description("How long to measure for, in milliseconds (default 1000).")] int durationMs = 1000)
        => $"""
            Set digital channel {digitalChannel} to output and drive it high, then read analog channel {analogChannel} for {durationMs} ms.

            Capture the analog channel across the transition if you can, so I can see what the output did to
            it, and tell me the level before and after. Drive the digital channel low again afterwards
            unless I say otherwise — leaving an output asserted is a surprise for whatever is wired to it.
            """;
}
