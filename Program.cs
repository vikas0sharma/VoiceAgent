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
using System.Collections.Concurrent;
using NAudio.Wave;
using Microsoft.Extensions.Configuration;

const int SampleRate = 24000;
const int Channels = 1;
const int BitsPerSample = 16;

Console.WriteLine("===========================================");
Console.WriteLine("   C# GenAI Live Audio Voice Bot Console");
Console.WriteLine("===========================================");
Console.WriteLine();

bool isVertex = args.Contains("--vertex", StringComparer.OrdinalIgnoreCase);
string model;
string mimeType;
Client client;

var configuration = new ConfigurationBuilder().AddUserSecrets<Program>().Build();

if (isVertex)
{
    Console.WriteLine("Running in Vertex AI mode.");
    string project = configuration["GOOGLE_CLOUD_PROJECT"] ??
                     throw new ArgumentNullException("GOOGLE_CLOUD_PROJECT not set for Vertex AI.");
    string location = System.Environment.GetEnvironmentVariable("GOOGLE_CLOUD_LOCATION") ?? "us-central1";
    client = new Client(project: project, location: location, vertexAI: true);
    model = "gemini-2.0-flash-live-preview-04-09";
    mimeType = "audio/l16;rate=24000";
}
else
{
    Console.WriteLine("Running in Gemini API mode.");
    string apiKey = configuration["GOOGLE_API_KEY"] ??
                    throw new ArgumentNullException("GOOGLE_API_KEY not set for Gemini API.");
    client = new Client(apiKey: apiKey);
    model = "gemini-2.5-flash-native-audio-preview-09-2025";
    mimeType = "audio/pcm";
}

Console.WriteLine($"Model: {model}");
Console.WriteLine();

var config = new LiveConnectConfig
{
    ResponseModalities = new List<Modality> { Modality.AUDIO },
    SpeechConfig = new SpeechConfig { LanguageCode = "en-US" },
    RealtimeInputConfig = new RealtimeInputConfig
    {
        AutomaticActivityDetection = new AutomaticActivityDetection
        {
            Disabled = true,
        }
    },
    SystemInstruction = new Content() { Parts = [new Part() { Text = "You are a helpful assistant named Aarvi." }] }
};

Console.WriteLine("Connecting to Gemini Live session...");
var geminiLiveSession = await client.Live.ConnectAsync(model, config);
Console.WriteLine("Connected to Gemini Live session.");
Console.WriteLine();

var cts = new CancellationTokenSource();
var audioPlaybackQueue = new BlockingCollection<byte[]>();

Console.CancelKeyPress += (sender, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\nShutting down...");
    cts.Cancel();
};

Console.WriteLine("Press ENTER to start/stop speaking, or Ctrl+C to exit.");
Console.WriteLine("===========================================");
Console.WriteLine();

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
                    Console.WriteLine("[Aarvi finished speaking. Your turn.]");
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
        Console.WriteLine($"Error receiving from Gemini: {ex.Message}");
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
                    Console.WriteLine();
                    Console.WriteLine("[Recording... Press ENTER to stop]");

                    // Signal start of turn
                    try
                    {
                        await geminiLiveSession.SendRealtimeInputAsync(
                            new LiveSendRealtimeInputParameters { ActivityStart = new ActivityStart { } });
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error sending activity start: {ex.Message}");
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
                                Console.WriteLine($"Error sending audio: {ex.Message}");
                            }
                        }
                    };

                    waveIn.StartRecording();
                }
                else
                {
                    // Stop recording
                    isRecording = false;
                    Console.WriteLine("[Stopped recording. Processing...]");

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
                        Console.WriteLine($"Error sending turn complete: {ex.Message}");
                    }

                    Console.WriteLine("[Waiting for Aarvi's response...]");
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

    Console.WriteLine("Closing Gemini session...");
    await geminiLiveSession.CloseAsync();
    Console.WriteLine("Gemini session closed.");

    try
    {
        await Task.WhenAll(receiveTask, playbackTask);
    }
    catch
    {
        // Ignore cancellation exceptions during cleanup
    }
}

Console.WriteLine("Goodbye!");