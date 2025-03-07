﻿/**
 *   Copyright (C) 2021 okaygo
 *
 *   https://github.com/misterokaygo/MapAssist/
 *
 *  This program is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, either version 3 of the License, or
 *  (at your option) any later version.
 *
 *  This program is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with this program.  If not, see <https://www.gnu.org/licenses/>.
 **/

using MapAssist.Settings;
using MapAssist.Types;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Path = System.IO.Path;

#pragma warning disable 649

namespace MapAssist.Helpers
{
    public class MapApi
    {
        private static readonly NLog.Logger _log = NLog.LogManager.GetCurrentClassLogger();
        private static Process _pipeClient;
        private static readonly object _pipeRequestLock = new object();
        private const string _procName = "MAServer.exe";

        private readonly ConcurrentDictionary<Area, AreaData> _cache;
        private Difficulty _difficulty;
        private uint _mapSeed;
        
        public static bool StartPipedChild()
        {
            // We have an exclusive lock on the MA process.
            // So we can kill off any previously lingering map servers
            // in case we had a weird shutdown that didn't clean up appropriately.
            StopPipeServers();

            var procFile = Path.Combine(Environment.CurrentDirectory, _procName);
            if (!File.Exists(procFile))
            {
                throw new Exception("Unable to start map server. Check Anti Virus settings.");
            }

            var path = FindD2();
            _pipeClient = new Process();
            _pipeClient.StartInfo.FileName = procFile;
            _pipeClient.StartInfo.Arguments = "\"" + path + "\"";
            _pipeClient.StartInfo.UseShellExecute = false;
            _pipeClient.StartInfo.RedirectStandardOutput = true;
            _pipeClient.StartInfo.RedirectStandardInput = true;
            _pipeClient.StartInfo.RedirectStandardError = true;
            _pipeClient.Start();

            var (startupLength, _) = MapApiRequest().Result;
            _log.Info($"{_procName} has started");

            return startupLength == 0;
        }
        
        private static string FindD2()
        {
            var providedPath = MapAssistConfiguration.Loaded.D2Path;
            if (!string.IsNullOrEmpty(providedPath))
            {
                if (Path.HasExtension(providedPath))
                {
                    throw new Exception("Provided D2 path is not set to a directory");
                }

                if (IsValidD2Path(providedPath))
                {
                    _log.Info("User provided D2 path is valid");
                    return providedPath;
                }

                _log.Info("User provided D2 path is invalid");
                throw new Exception("Provided D2 path is not the correct version");
            }
            
            var installPath = Registry.GetValue("HKEY_CURRENT_USER\\SOFTWARE\\Blizzard Entertainment\\Diablo II", "InstallPath", "INVALID") as string;
            if (installPath == "INVALID" || !IsValidD2Path(installPath))
            {
                _log.Info("Registry-provided D2 path not found or invalid");
                throw new Exception("Unable to automatically locate D2 installation. Please provide path manually in the config at `D2Path`.");
            }
            
            _log.Info("Registry-provided D2 path is valid");
            return installPath;
        }

        private static bool IsValidD2Path(string path)
        {
            try
            {
                var gamePath = Path.Combine(path, "game.exe");
                var version = FileVersionInfo.GetVersionInfo(gamePath);
                return version.FileMajorPart == 1 && version.FileMinorPart == 0 && version.FileBuildPart == 13 &&
                       version.FilePrivatePart == 60;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static async Task<(uint, string)> MapApiRequest(byte[] writeBytes = null, int timeout = 1000)
        {
            if (disposed || _pipeClient.HasExited)
            {
                return (0, null);
            }

            var pipeInput = _pipeClient.StandardInput;
            var pipeOutput = _pipeClient.StandardOutput;

            Func<int, Task<byte[]>> ReadBytes = async (readBytesLength) =>
            {
                var data = new byte[0];
                var cts = new CancellationTokenSource(timeout);

                while (!disposed && !_pipeClient.HasExited && !cts.IsCancellationRequested && data.Length < readBytesLength)
                {
                    var tryReadLength = readBytesLength - data.Length;
                    var chunk = new byte[tryReadLength];
                    var dataReadLength = await pipeOutput.BaseStream.ReadAsync(chunk, 0, tryReadLength, cts.Token);

                    data = Combine(data, chunk.Take(dataReadLength).ToArray());
                }

                var response = !disposed && !_pipeClient.HasExited && !cts.IsCancellationRequested ? data : null;
                cts.Dispose();
                return response;
            };

            Func<int, Task<byte[]>> TryReadBytes = async (readBytesLength) =>
            {
                var task = ReadBytes(readBytesLength);
                var result = await Task.WhenAny(task, Task.Delay(timeout));
                if (result == task)
                {
                    return await task;
                }
                else
                {
                    return null;
                }
            };

            if (writeBytes != null)
            {
                pipeInput.BaseStream.Write(writeBytes, 0, writeBytes.Length);
                pipeInput.BaseStream.Flush();
            }

            var readLength = await TryReadBytes(4);
            if (readLength == null) return (0, null);
            var length = BitConverter.ToUInt32(readLength, 0);

            if (length == 0)
            {
                return (0, null);
            }

            string json = null;
            JObject jsonObj;
            try
            {
                _log.Info($"Reading {length} bytes from {_procName}");
                var readJson = await TryReadBytes((int)length);
                if (readJson == null) return (0, null);
                json = Encoding.UTF8.GetString(readJson);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return (length, null);
                }
                jsonObj = JObject.Parse(json);
            }
            catch (Exception e)
            {
                _log.Error(e);
                _log.Error(e, "Unable to parse JSON data from map server.");
                if (!string.IsNullOrWhiteSpace(json))
                {
                    _log.Error(json);
                }

                return (length, null);
            }

            if (jsonObj.ContainsKey("error"))
            {
                _log.Error(jsonObj["error"].ToString());
                return (length, null);
            }

            return (length, json);
        }

        public MapApi(Difficulty difficulty, uint mapSeed)
        {
            _difficulty = difficulty;
            _mapSeed = mapSeed;

            // Cache for pre-fetching maps for the surrounding areas.
            _cache = new ConcurrentDictionary<Area, AreaData>();

            Prefetch(MapAssistConfiguration.Loaded.PrefetchAreas);
        }

        public AreaData GetMapData(Area area)
        {
            _log.Info($"Requesting MapSeed: {_mapSeed} Area: {area} Difficulty: {_difficulty}");

            if (!_cache.TryGetValue(area, out AreaData areaData))
            {
                // Not in the cache, block.
                _log.Info($"Cache miss on {area}");
                areaData = GetMapDataInternal(area);
                _cache[area] = areaData;
            } else
            {
                _log.Info($"Cache found on {area}");
            }

            if (areaData != null)
            {
                _log.Info($"Prefetching areas adjacent to {area}");
                Area[] adjacentAreas = areaData.AdjacentLevels.Keys.ToArray();
                if (adjacentAreas.Any())
                {
                    _log.Info($"Adjacent areas to {area} found");
                    Prefetch(adjacentAreas);
                } else
                {
                    _log.Info($"No adjacent areas to {area} found");
                }
            } else
            {
                _log.Info($"areaData was null on {area}");
            }

            return areaData;
        }

        private void Prefetch(params Area[] areas)
        {
            var prefetchBackgroundWorker = new BackgroundWorker();
            prefetchBackgroundWorker.DoWork += (sender, args) =>
            {
                if (MapAssistConfiguration.Loaded.ClearPrefetchedOnAreaChange)
                {
                    _cache.Clear();
                }

                // Special value telling us to exit.
                if (areas.Length == 0)
                {
                    _log.Info("Prefetch worker terminating");
                    return;
                }

                foreach (Area area in areas)
                {
                    if (_cache.ContainsKey(area)) continue;

                    _cache[area] = GetMapDataInternal(area);
                    _log.Info($"Prefetched {area}");
                }
            };
            prefetchBackgroundWorker.RunWorkerAsync();
            prefetchBackgroundWorker.Dispose();
        }

        private AreaData GetMapDataInternal(Area area)
        {
            var req = new Req();
            req.seed = _mapSeed;
            req.difficulty = (uint)_difficulty;
            req.levelId = (uint)area;

            lock (_pipeRequestLock)
            {
                uint length = 0;
                string json = null;
                var retry = false;

                do
                {
                    retry = false;

                    (length, json) = MapApiRequest(ToBytes(req)).Result;

                    if (json == null)
                    {
                        _log.Error($"Unable to load data for {area} from {_procName}, retrying after restarting {_procName}");
                        StartPipedChild();
                        retry = true;
                    }
                } while (retry);

                var rawAreaData = JsonConvert.DeserializeObject<RawAreaData>(json);
                return rawAreaData.ToInternal(area);
            }
        }

        public static byte[] Combine(byte[] first, byte[] second)
        {
            var ret = new byte[first.Length + second.Length];
            Buffer.BlockCopy(first, 0, ret, 0, first.Length);
            Buffer.BlockCopy(second, 0, ret, first.Length, second.Length);
            return ret;
        }

        private static byte[] ToBytes(Req req)
        {
            var size = Marshal.SizeOf(req);
            var arr = new byte[size];

            IntPtr ptr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(req, ptr, true);
            Marshal.Copy(ptr, arr, 0, size);
            Marshal.FreeHGlobal(ptr);
            return arr;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct Req
        {
            public uint seed;
            public uint difficulty;
            public uint levelId;
        }

        private static bool disposed = false;
        public static void Dispose()
        {
            if (!disposed)
            {
                disposed = true;
                DisposePipe();
            }
        }

        private static void DisposePipe()
        {
            if (_pipeClient == null)
            {
                return;
            }

            _log.Info("Closing map server");
            if (!_pipeClient.HasExited)
            {
                try { _pipeClient.Kill(); } catch (Exception) { }
                try { _pipeClient.Close(); } catch (Exception) { }
            }
            try { _pipeClient.Dispose(); } catch (Exception) { }

            _pipeClient = null;
        }

        private static void StopPipeServers()
        {
            DisposePipe();

            // Shutdown old running versions of the map server
            var procs = Process.GetProcessesByName(_procName);
            foreach (var proc in procs)
            {
                proc.Kill();
            }
        }
    }
}
