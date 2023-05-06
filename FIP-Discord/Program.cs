using Discord;
using Discord.Audio;
using Discord.WebSocket;
using FIP_Discord;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Web;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace FIP
{
    public sealed class Program
    {
        private readonly DiscordSocketClient _client = new(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates
        });

        public static async Task Main()
        {
            await new Program().StartAsync();
        }

        private string _openRadioApiToken;
        private string _lastFmApiToken;
        private readonly HttpClient _http = new();

        public async Task StartAsync()
        {
            await Log.LogAsync(new LogMessage(LogSeverity.Info, "Setup", "Initialising bot"));

            // Setting Logs callback
            _client.Log += Log.LogAsync;

            // Load credentials
            if (!File.Exists("Keys/Credentials.json"))
                throw new FileNotFoundException("Missing Credentials file");
            var credentials = JsonSerializer.Deserialize<Credentials>(File.ReadAllText("Keys/Credentials.json"));
            if (credentials == null || credentials.BotToken == null || credentials.OpenRadioApiToken == null || credentials.LastFmApiToken == null)
                throw new NullReferenceException("Missing credentials");
            _openRadioApiToken = credentials.OpenRadioApiToken;
            _lastFmApiToken = credentials.LastFmApiToken;

            // Init info for all FIPs
            foreach (var fip in (FIPChannel[])Enum.GetValues(typeof(FIPChannel)))
            {
                _currentSong.Add(fip, null);
            }

                _client.Ready += Ready;
            _client.SlashCommandExecuted += SlashCommandExecuted;

            await _client.LoginAsync(TokenType.Bot, credentials.BotToken);
            await _client.StartAsync();

            new Thread(new ThreadStart(WatchOver)).Start();

            // We keep the bot online
            await Task.Delay(-1);
        }

        private readonly Dictionary<ulong, (IMessageChannel MessageChannel, FIPChannel FIPChannel)> _followChans = new();
        private readonly Dictionary<ulong, SocketVoiceChannel> _audioChannels = new();
        private readonly Dictionary<FIPChannel, Song?> _currentSong = new();
        private void WatchOver()
        {
            while (Thread.CurrentThread.IsAlive)
            {
                while (_followChans.Any())
                {
                    foreach (var fip in _followChans.Select(x => x.Value.FIPChannel).Distinct())
                    {
                        var previous = _currentSong[fip]?.end;
                        UpdateCurrentSongAsync(fip).GetAwaiter().GetResult();
                        var song = _currentSong[fip];
                        if (song != null && previous != song.end)
                        {
                            foreach (var chan in _followChans)
                            {
                                if (chan.Value.FIPChannel == fip) // TODO: Can prob be improved
                                {
                                    var embed = GetSongEmbedAsync(chan.Value.FIPChannel).GetAwaiter().GetResult();

                                    try
                                    {
                                        chan.Value.MessageChannel.SendMessageAsync(embed: embed).GetAwaiter().GetResult();
                                    }
                                    catch( Exception e){

                                        Log.LogErrorAsync(e).GetAwaiter().GetResult();
                                    }
                                }
                            }
                        }
                        Thread.Sleep(1000);
                    }
                    Thread.Sleep(10);
                }
                Thread.Sleep(200);
            }
        }

        private async Task UpdateCurrentSongAsync(FIPChannel fip)
        {
            TimeSpan t = DateTime.UtcNow - new DateTime(1970, 1, 1);
            int epochTime = (int)t.TotalSeconds;
            if (_currentSong[fip] != null && _currentSong[fip]!.end >= epochTime)
            {
                return;
            }
            var json = JsonSerializer.Serialize(new GraphQL
            {
                query = "{ live(station: " + FIPChannelInfo.GetStationName(fip) + ") { song { end, track {title albumTitle mainArtists} } } }"
            });

            var answer = await _http.PostAsync($"https://openapi.radiofrance.fr/v1/graphql?x-token={_openRadioApiToken}", new StringContent(json, Encoding.UTF8, "application/json"));
            answer.EnsureSuccessStatusCode();

            var text = await answer.Content.ReadAsStringAsync();
            _currentSong[fip] = JsonSerializer.Deserialize<GraphQLResult>(text).data.live.song;
            await Log.LogAsync(new LogMessage(LogSeverity.Info, "Song Updater", $"Updated song for {fip}: {(_currentSong[fip] == null ? "No data" : _currentSong[fip].track.title)}"));
        }

        private async Task<Embed> GetSongEmbedAsync(FIPChannel fip)
        {
            var song = _currentSong[fip];
            var lastFm = JsonSerializer.Deserialize<LastFm>(await _http.GetStringAsync($"https://ws.audioscrobbler.com/2.0/?method=track.getInfo&api_key={HttpUtility.UrlEncode(_lastFmApiToken)}&artist={song.track.mainArtists.FirstOrDefault()}&track={HttpUtility.UrlEncode(song.track.title)}&format=json"));
            return new EmbedBuilder
            {
                Title = $"{song.track.title} by {string.Join(", ", song.track.mainArtists)}",
                Fields = new()
                {
                    new()
                    {
                        Name = "Album",
                        Value = song.track.albumTitle,
                        IsInline = true
                    },
                    new()
                    {
                        Name = "Ends",
                        Value = $"<t:{song.end}:R>",
                        IsInline = true
                    }
                },
                ImageUrl = lastFm?.track?.album?.image?.LastOrDefault()?.text,
                Url = lastFm?.track?.url,
                Color = new(227, 0, 123),
                Footer = new()
                {
                    Text = $"You are listening to {fip}"
                }
            }.Build();
        }

        private async Task SlashCommandExecuted(SocketSlashCommand arg)
        {
            if (arg.CommandName == "play")
            {
                if (arg.User is not IGuildUser guildUser)
                {
                    await arg.RespondAsync("This command can only be done in a guild", ephemeral: true);
                    return;
                }
                if (guildUser.VoiceChannel == null)
                {
                    await arg.RespondAsync("You must be in a voice channel to do this command", ephemeral: true);
                    return;
                }
                var channel = (FIPChannel)Convert.ToInt32(arg.Data.Options.FirstOrDefault(x => x.Name == "channel")?.Value ?? 0);
                await arg.RespondAsync("Starting the radio...");
                _ = Task.Run(async () =>
                {
                    var fipChan = channel;
                    var vChan = (SocketVoiceChannel)guildUser.VoiceChannel;
                    var timer = new System.Timers.Timer()
                    {
                        Interval = 10000
                    };
                    timer.Elapsed += async (sender, _) =>
                    {
                        if (!_audioChannels.ContainsKey(arg.GuildId.Value)) // We already used the stop command to end the radio
                        {
                            return;
                        }
                        if (vChan.ConnectedUsers.Count == 1)
                        {
                            await arg.Channel.SendMessageAsync("No user left in the channel, ending radio...");
                            await vChan.DisconnectAsync();
                            _followChans.Remove(arg.Channel.Id);
                            _audioChannels.Remove(arg.GuildId.Value);
                            timer.Enabled = false;
                        }
                    };
                    timer.Start();
                    await UpdateCurrentSongAsync(channel);
                    await arg.Channel.SendMessageAsync(embed: await GetSongEmbedAsync(channel));
                    if (_followChans.ContainsKey(arg.Channel.Id))
                    {
                        _followChans[arg.Channel.Id] = (arg.Channel, channel);
                    }
                    else
                    {
                        _followChans.Add(arg.Channel.Id, (arg.Channel, channel));
                    }
                    if (_audioChannels.ContainsKey(arg.GuildId!.Value))
                    {
                        _audioChannels[arg.GuildId!.Value] = vChan;
                    }
                    else
                    {
                        _audioChannels.Add(arg.GuildId!.Value, vChan);
                    }
                    try
                    {
                        var audioClient = await guildUser.VoiceChannel.ConnectAsync();
                        var ffmpeg = Process.Start(new ProcessStartInfo
                        {
                            FileName = "ffmpeg",
                            Arguments = $"-hide_banner -loglevel panic -i {FIPChannelInfo.GetStreamFlux(channel)} -ac 2 -f s16le -ar 48000 pipe:",
                            UseShellExecute = false,
                            RedirectStandardOutput = true
                        });
                        using var output = ffmpeg.StandardOutput.BaseStream;
                        using var discord = audioClient.CreatePCMStream(AudioApplication.Mixed);
                        try { await output.CopyToAsync(discord); }
                        catch (Exception ex)
                        {
                            await Log.LogErrorAsync(ex);
                        }
                        finally
                        {
                            await discord.FlushAsync();
                        }
                    }
                    catch (TaskCanceledException)
                    {
                        await arg.Channel.SendMessageAsync("Failed to connect to the text channel, please make sure I have the right permissions");
                    }
                    catch (Exception ex)
                    {
                        await Log.LogErrorAsync(ex);
                    }
                    finally
                    {
                        if (_followChans.TryGetValue(arg.Channel.Id, out (IMessageChannel MessageChannel, FIPChannel FIPChannel) value) && value.FIPChannel == fipChan)
                        {
                            _followChans.Remove(arg.Channel.Id);
                            _audioChannels.Remove(arg.GuildId.Value);
                        }
                    }
                });
            }
            else if (arg.CommandName == "program")
            {
                await arg.RespondAsync(embed: new EmbedBuilder
                {
                    Title = "Program",
                    Description = string.Join("\n", ((FIPChannel[])Enum.GetValues(typeof(FIPChannel))).Select(x =>
                    {
                        UpdateCurrentSongAsync(x).GetAwaiter().GetResult();
                        if (_currentSong[x] == null)
                        {
                            return $"{x}: No data";
                        }
                        return $"{x}: {_currentSong[x].track.title} by {string.Join(", ", _currentSong[x].track.mainArtists)}";
                    })),
                    Color = new(227, 0, 123)
                }.Build());
            }
            else if (arg.CommandName == "stop")
            {
                if (arg.GuildId == null)
                {
                    await arg.RespondAsync("This command can only be done in a guild", ephemeral: true);
                }
                else if (!_audioChannels.ContainsKey(arg.GuildId.Value))
                {
                    await arg.RespondAsync("No radio was start in this server", ephemeral: true);
                }
                else
                {
                    await arg.RespondAsync("Stopping the radio...");
                    await _audioChannels[arg.GuildId.Value].DisconnectAsync();
                    _followChans.Remove(arg.Channel.Id);
                    _audioChannels.Remove(arg.GuildId.Value);
                }
            }
            else if (arg.CommandName == "github")
            {
                await arg.RespondAsync("https://github.com/Xwilarg/FIP-Discord/", ephemeral: true);
            }
            else if (arg.CommandName == "invite")
            {
                await arg.RespondAsync("https://discord.com/api/oauth2/authorize?client_id=1062043607252606976&permissions=3145728&scope=bot%20applications.commands", ephemeral: true);
            }
        }

        private bool _isInit = false;
        private async Task Ready()
        {
            if (!_isInit)
            {
                _isInit = true;
                _ = Task.Run(async () =>
                {
                    var commands = new SlashCommandBuilder[]
                    {
                        new()
                        {
                            Name = "play",
                            Description = "Start playing the radio in the vocal channel where the user is",
                            Options = new()
                            {
                                new()
                                {
                                    Name = "channel",
                                    Description = "FIP Channel to listen to",
                                    IsRequired = false,
                                    Type = ApplicationCommandOptionType.Integer,
                                    Choices = ((FIPChannel[])Enum.GetValues(typeof(FIPChannel))).Select(x => new ApplicationCommandOptionChoiceProperties()
                                    {
                                        Name = x.ToString(),
                                        Value = (int)x
                                    }).ToList()
                                }
                            }
                        },
                        new()
                        {
                            Name = "program",
                            Description = "Display the various music currently playing on FIP"
                        },
                        new()
                        {
                            Name = "stop",
                            Description = "Stop playing the radio"
                        },
                        new()
                        {
                            Name = "github",
                            Description = "Get the link to the source code of the bot"
                        },
                        new()
                        {
                            Name = "invite",
                            Description = "Get the invite link of the bot"
                        }
                    }.Select(x => x.Build()).ToArray();
                    foreach (var cmd in commands)
                    {
                        if (Debugger.IsAttached)
                        {
                            // That's where I debug my stuffs, yeah I'm sorry about hardcoding stuffs
                            await _client.GetGuild(832001341865197579).CreateApplicationCommandAsync(cmd);
                        }
                        else
                        {
                            await _client.CreateGlobalApplicationCommandAsync(cmd);
                        }
                    }
                    if (Debugger.IsAttached)
                    {
                        await _client.GetGuild(832001341865197579).BulkOverwriteApplicationCommandAsync(commands);
                    }
                    else
                    {
                        await _client.BulkOverwriteGlobalApplicationCommandsAsync(commands);
                    }
                });
            }
        }
    }
}