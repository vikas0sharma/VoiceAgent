/*
 * Copyright 2025 Google LLC
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *      https://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.Configuration;
using NAudio.Wave;
using Spectre.Console;
using System.Collections.Concurrent;

const int SampleRate = 24000;
const int Channels = 1;
const int BitsPerSample = 16;

// Enable UTF-8 output for emoji support
Console.OutputEncoding = System.Text.Encoding.UTF8;

// Status tracking for animations
var currentStatus = new StatusTracker();

// Beautiful header
AnsiConsole.Write(
    new FigletText("Voice Agent")
        .Centered()
        .Color(Color.Cyan1));

AnsiConsole.Write(new Rule("[cyan]C# GenAI Live Audio Voice Bot[/]").RuleStyle("blue"));
AnsiConsole.WriteLine();

bool isVertex = args.Contains("--vertex", StringComparer.OrdinalIgnoreCase);
string model;
string mimeType;
Client client;

var configuration = new ConfigurationBuilder().AddUserSecrets<Program>().Build();

if (isVertex)
{
    AnsiConsole.MarkupLine("☁️ [blue]Running in Vertex AI mode[/]");
    string project = configuration["GOOGLE_CLOUD_PROJECT"] ??
                     throw new ArgumentNullException("GOOGLE_CLOUD_PROJECT not set for Vertex AI.");
    string location = System.Environment.GetEnvironmentVariable("GOOGLE_CLOUD_LOCATION") ?? "us-central1";
    client = new Client(project: project, location: location, vertexAI: true);
    model = "gemini-2.0-flash-live-preview-04-09";
    mimeType = "audio/l16;rate=24000";
}
else
{
    AnsiConsole.MarkupLine("✨ [magenta]Running in Gemini API mode[/]");
    string apiKey = configuration["GOOGLE_API_KEY"] ??
                    throw new ArgumentNullException("GOOGLE_API_KEY not set for Gemini API.");
    client = new Client(apiKey: apiKey);
    model = "gemini-2.5-flash-native-audio-preview-09-2025";
    mimeType = "audio/pcm";
}

AnsiConsole.MarkupLine($"🤖 [grey]Model:[/] [yellow]{model}[/]");
AnsiConsole.WriteLine();

var config = new LiveConnectConfig
{
    ResponseModalities = new List<Modality> { Modality.AUDIO },
    SpeechConfig = new SpeechConfig { LanguageCode = "en-US" },
    RealtimeInputConfig = new RealtimeInputConfig
    {
        AutomaticActivityDetection = new AutomaticActivityDetection
        {
            Disabled = true,
        },

    },
    SystemInstruction = new Content() { Parts = [new Part() { Text = "You are a helpful assistant named Aarvi." }] },
    Tools = new List<Tool>
    {
        new Tool()
        {
            GoogleSearch = new GoogleSearch(),
        },
    },
    OutputAudioTranscription = new AudioTranscriptionConfig(),
    InputAudioTranscription = new AudioTranscriptionConfig()

};

await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .SpinnerStyle(Style.Parse("green"))
    .StartAsync("Connecting to Gemini Live session...", async ctx =>
    {
        await Task.Delay(500); // Brief visual delay for the spinner
    });

AnsiConsole.MarkupLine("[green]✅ Connected to Gemini Live session![/]");
var geminiLiveSession = await client.Live.ConnectAsync(model, config);
AnsiConsole.WriteLine();

var cts = new CancellationTokenSource();
var audioPlaybackQueue = new BlockingCollection<byte[]>();

Console.CancelKeyPress += (sender, e) =>
{
    e.Cancel = true;
    currentStatus.Stop();
    AnsiConsole.MarkupLine("\n👋 [yellow]Shutting down...[/]");
    cts.Cancel();
};

// Instructions panel
var instructionPanel = new Panel(
    new Markup("[white]Press [green]ENTER[/] to start/stop speaking\nPress [red]Ctrl+C[/] to exit[/]"))
    .Header("[cyan]Instructions[/]")
    .BorderColor(Color.Cyan1)
    .Padding(1, 0);
AnsiConsole.Write(instructionPanel);
AnsiConsole.WriteLine();

AnsiConsole.Write(new Rule().RuleStyle("grey"));
AnsiConsole.WriteLine();

// Status animation task
var animationTask = Task.Run(async () =>
{
    // Using direct Unicode emoji characters for better compatibility
    var userSpeakingFrames = new[] { "🎤", "🎙️", "🎤", "🎵" };
    var agentSpeakingFrames = new[] { "🤖", "💬", "🤖", "💭" };
    var waitingFrames = new[] { "⏳", "⌛" };
    int frameIndex = 0;

    while (!cts.Token.IsCancellationRequested)
    {
        try
        {
            if (currentStatus.IsUserSpeaking)
            {
                AnsiConsole.Markup($"\r[red]{userSpeakingFrames[frameIndex % userSpeakingFrames.Length]} Recording...[/]    ");
            }
            else if (currentStatus.IsWaitingForAgent)
            {
                AnsiConsole.Markup($"\r[yellow]{waitingFrames[frameIndex % waitingFrames.Length]} Waiting for Aarvi...[/]    ");
            }
            else if (currentStatus.IsAgentSpeaking)
            {
                AnsiConsole.Markup($"\r[cyan]{agentSpeakingFrames[frameIndex % agentSpeakingFrames.Length]} Aarvi is speaking...[/]    ");
            }

            frameIndex++;
            await Task.Delay(300, cts.Token);
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }
}, cts.Token);

// Audio playback task
var playbackTask = Task.Run(() =>
{
    using var waveOut = new WaveOutEvent();
    var bufferedWaveProvider = new BufferedWaveProvider(new WaveFormat(SampleRate, BitsPerSample, Channels))
    {
        BufferDuration = TimeSpan.FromSeconds(30),
        DiscardOnBufferOverflow = true
    };
    waveOut.Init(bufferedWaveProvider);
    waveOut.Play();

    try
    {
        foreach (var audioData in audioPlaybackQueue.GetConsumingEnumerable(cts.Token))
        {
            bufferedWaveProvider.AddSamples(audioData, 0, audioData.Length);
        }
    }
    catch (OperationCanceledException)
    {
        // Expected when cancellation is requested
    }

    waveOut.Stop();
}, cts.Token);

// Receive responses from Gemini
var receiveTask = Task.Run(async () =>
{
    try
    {
        while (!cts.Token.IsCancellationRequested)
        {
            var serverMsg = await geminiLiveSession.ReceiveAsync();
            if (serverMsg?.ServerContent != null)
            {
                var content = serverMsg.ServerContent;
                if (content.ModelTurn?.Parts != null)
                {
                    currentStatus.SetAgentSpeaking();
                    foreach (var part in content.ModelTurn.Parts)
                    {
                        if (part.InlineData?.MimeType?.StartsWith("audio/") == true && part.InlineData.Data != null)
                        {
                            audioPlaybackQueue.Add(part.InlineData.Data);
                        }
                    }
                }

                if (content.TurnComplete == true)
                {
                    currentStatus.Stop();
                    AnsiConsole.MarkupLine("\r[green]✅ Aarvi finished speaking. Your turn![/]                    ");
                }
            }
        }
    }
    catch (OperationCanceledException)
    {
        // Expected when cancellation is requested
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"\r[red]❌ Error receiving from Gemini: {ex.Message}[/]");
    }
}, cts.Token);

// Main conversation loop
bool isRecording = false;
WaveInEvent? waveIn = null;

try
{
    while (!cts.Token.IsCancellationRequested)
    {
        if (Console.KeyAvailable)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                if (!isRecording)
                {
                    // Start recording
                    isRecording = true;
                    currentStatus.SetUserSpeaking();
                    AnsiConsole.WriteLine();

                    // Signal start of turn
                    try
                    {
                        await geminiLiveSession.SendRealtimeInputAsync(
                            new LiveSendRealtimeInputParameters { ActivityStart = new ActivityStart { } });
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"\r[red]❌ Error sending activity start: {ex.Message}[/]");
                    }

                    waveIn = new WaveInEvent
                    {
                        WaveFormat = new WaveFormat(SampleRate, BitsPerSample, Channels),
                        BufferMilliseconds = 100
                    };

                    waveIn.DataAvailable += async (sender, e) =>
                    {
                        if (e.BytesRecorded > 0 && !cts.Token.IsCancellationRequested)
                        {
                            var audioData = new byte[e.BytesRecorded];
                            Array.Copy(e.Buffer, audioData, e.BytesRecorded);

                            try
                            {
                                var realtimeInput = new LiveSendRealtimeInputParameters
                                {
                                    Audio = new Google.GenAI.Types.Blob
                                    {
                                        Data = audioData,
                                        MimeType = mimeType,
                                    }
                                };
                                await geminiLiveSession.SendRealtimeInputAsync(realtimeInput);
                            }
                            catch (Exception ex)
                            {
                                AnsiConsole.MarkupLine($"\r[red]❌ Error sending audio: {ex.Message}[/]");
                            }
                        }
                    };

                    waveIn.StartRecording();
                }
                else
                {
                    // Stop recording
                    isRecording = false;
                    currentStatus.SetWaitingForAgent();
                    AnsiConsole.MarkupLine("\r[grey]⏹️ Stopped recording. Processing...[/]                    ");

                    waveIn?.StopRecording();
                    waveIn?.Dispose();
                    waveIn = null;

                    // Signal end of turn
                    try
                    {
                        await geminiLiveSession.SendRealtimeInputAsync(
                            new LiveSendRealtimeInputParameters { ActivityEnd = new ActivityEnd { } });
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"\r[red]❌ Error sending turn complete: {ex.Message}[/]");
                    }
                }
            }
        }

        await Task.Delay(50, cts.Token);
    }
}
catch (OperationCanceledException)
{
    // Expected when Ctrl+C is pressed
}
finally
{
    waveIn?.StopRecording();
    waveIn?.Dispose();

    audioPlaybackQueue.CompleteAdding();
    currentStatus.Stop();

    AnsiConsole.WriteLine();
    AnsiConsole.Write(new Rule("[yellow]Closing Session[/]").RuleStyle("yellow"));

    await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .SpinnerStyle(Style.Parse("yellow"))
        .StartAsync("Closing Gemini session...", async ctx =>
        {
            await geminiLiveSession.CloseAsync();
        });

    AnsiConsole.MarkupLine("[green]✅ Gemini session closed.[/]");

    try
    {
        await Task.WhenAll(receiveTask, playbackTask, animationTask);
    }
    catch
    {
        // Ignore cancellation exceptions during cleanup
    }
}

AnsiConsole.WriteLine();
AnsiConsole.MarkupLine("[cyan]👋 Goodbye![/]");

// Helper class to track current status for animations
class StatusTracker
{
    public bool IsUserSpeaking { get; private set; }
    public bool IsAgentSpeaking { get; private set; }
    public bool IsWaitingForAgent { get; private set; }

    public void SetUserSpeaking()
    {
        IsUserSpeaking = true;
        IsAgentSpeaking = false;
        IsWaitingForAgent = false;
    }

    public void SetAgentSpeaking()
    {
        IsUserSpeaking = false;
        IsAgentSpeaking = true;
        IsWaitingForAgent = false;
    }

    public void SetWaitingForAgent()
    {
        IsUserSpeaking = false;
        IsAgentSpeaking = false;
        IsWaitingForAgent = true;
    }

    public void Stop()
    {
        IsUserSpeaking = false;
        IsAgentSpeaking = false;
        IsWaitingForAgent = false;
    }
}
