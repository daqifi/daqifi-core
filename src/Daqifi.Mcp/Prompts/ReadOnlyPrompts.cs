using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Daqifi.Mcp.Prompts;

/// <summary>
/// The jobs a <c>--read-only</c> server can still carry out, so they are the only prompts it
/// offers.
/// </summary>
/// <remarks>
/// Retrieving a log is the one job that survives read-only: <c>list_sd_files</c> and
/// <c>download_sd_file</c> are both allowed, and neither is refused while a log is running. Only
/// the opening stop is — a log can already be running because something else started it (the
/// desktop app, another client, or a session before this server launched), and read-only refuses
/// <c>stop_sd_logging</c>, so the prompt says what to do when that happens instead of assuming it
/// cannot. Everything in <see cref="DaqifiPrompts"/> opens by enabling channels or setting a
/// rate, which read-only refuses outright, so a read-only server that listed them would be
/// advertising four jobs it will not do. That is the same reasoning
/// <see cref="ServerOptions.Instructions"/> applies to the read-only rule itself.
/// </remarks>
[McpServerPromptType]
public static class ReadOnlyPrompts
{
    [McpServerPrompt(Name = "summarize_sd_log", Title = "Download and summarize an SD log")]
    [Description("Stop SD-card logging if it is running, download a log file, and summarize the data in it.")]
    public static string SummarizeSdLog(
        [Description("The log file on the card. Leave empty to list the card and use the most recent one.")] string fileName = "",
        [Description("The channel to summarize, e.g. 'AI0'. Leave empty to summarize every channel.")] string channel = "")
    {
        var which = string.IsNullOrWhiteSpace(fileName)
            ? "list the files on the card and take the most recent one"
            : $"download '{fileName}'";
        var what = string.IsNullOrWhiteSpace(channel)
            ? "every channel in it"
            : channel;

        return $"""
            If the device is still logging, stop it first so the file is complete. A read-only server
            refuses to stop it: in that case say so and carry on with the file as it stands, noting
            that it was still being written.

            Then {which} and tell me the average on {what}.

            Download with the CSV export on so you can read the values back. Also tell me how many samples
            arrived versus how many the duration and sample rate imply, and say so plainly if the file came
            back empty rather than reporting an average of nothing.
            """;
    }
}
