// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using TS3AudioBot.Config;
using TS3AudioBot.Environment;
using TS3AudioBot.Helper;
using TS3AudioBot.Localization;
using TS3AudioBot.ResourceFactories;
using TSLib.Helper;

namespace TS3AudioBot.Audio {
	/// <summary>Provides a convenient inferface for enqueing, playing and registering song events.</summary>
	public class PlayManager {
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

		private readonly ConfBot confBot;
		private readonly Player playerConnection;
		private readonly ResolveContext resourceResolver;
		private readonly Stats stats;

		public object Lock { get; } = new object();

		public PlayInfoEventArgs CurrentPlayData { get; private set; }
		public bool IsPlaying => CurrentPlayData != null;
		public bool AutoStartPlaying { get; set; } = true;
		public PlayQueue Queue { get; } = new PlayQueue();

		public event EventHandler<PlayInfoEventArgs> OnResourceUpdated;
		public event EventHandler<PlayInfoEventArgs> BeforeResourceStarted;
		public event EventHandler<PlayInfoEventArgs> AfterResourceStarted;
		public event EventHandler<SongEndEventArgs> ResourceStopped;
		public event EventHandler PlaybackStopped;

		private readonly SongAnalyzer songAnalyzer;

		public PlayManager(ConfBot config, Player playerConnection, ResolveContext resourceResolver, Stats stats) {
			confBot = config;
			this.playerConnection = playerConnection;
			this.resourceResolver = resourceResolver;
			this.stats = stats;
			songAnalyzer = new SongAnalyzer(resourceResolver, playerConnection.FfmpegProducer);

			playerConnection.FfmpegProducer.OnSongLengthParsed += (sender, args) => {
				lock (Lock) {
					if (songAnalyzer.Instance != null) {
						Log.Info("Preparing song analyzer... (OnSongLengthParsed)");
						songAnalyzer.Prepare(GetAnalyzeTaskStartTime());
					}
				}
			};
		}

		private int GetAnalyzeTaskStartTime() {
			return SongAnalyzer.GetTaskStartTime(playerConnection.Length - playerConnection.Position);
		}

		public void Clear() {
			lock (Lock) {
				Queue.Clear();
				TryStopCurrentSong();
				OnPlaybackEnded();
				songAnalyzer.Clear();
			}
		}

		public E<LocalStr> Play() {
			lock (Lock) {
				return TryPlay(true);
			}
		}

		public void EnqueueAsNextSong(QueueItem item) {
			lock (Lock) {
				Queue.InsertAfter(item, Queue.Index);
				UpdateNextSong();
			}
		}

		public void RemoveAt(int index) {
			lock (Lock) {
				Queue.Remove(index);
				UpdateNextSong();
			}
		}

		public void RemoveRange(int from, int to) {
			lock (Lock) {
				Queue.RemoveRange(from, to);
				UpdateNextSong();
			}
		}

		public E<LocalStr> Enqueue(string url, MetaData meta, string audioType = null) {
			var result = resourceResolver.Load(url, audioType);
			if (!result.Ok) {
				stats.TrackSongLoad(audioType, false, true);
				return result.Error;
			}

			return Enqueue(result.Value.BaseData, meta);
		}

		public E<LocalStr> Enqueue(AudioResource ar, MetaData meta) => Enqueue(new QueueItem(ar, meta));

		public E<LocalStr> Enqueue(IEnumerable<AudioResource> items, MetaData meta) {
			return Enqueue(items.Select(res => new QueueItem(res, meta)));
		}

		public E<LocalStr> Enqueue(QueueItem item) {
			lock (Lock) {
				Queue.Enqueue(item);
				var res = TryInitialStart(true);
				if(res.Ok)
					UpdateNextSong();
				return res;
			}
		}

		public E<LocalStr> Enqueue(IEnumerable<QueueItem> items) {
			lock (Lock) {
				Queue.Enqueue(items);
				var res = TryInitialStart(true);
				if(res.Ok)
					UpdateNextSong();
				return res;
			}
		}

		public E<LocalStr> Next(int count = 1) {
			if (count <= 0)
				return R.Ok;
			lock (Lock) {
				Log.Info("Skip {0} songs requested", count);
				TryStopCurrentSong();
				Queue.Skip(count);
				return TryPlay(false);
			}
		}

		public E<LocalStr> Next() {
			lock (Lock) {
				Log.Debug("Next song requested");
				TryStopCurrentSong();
				if (!Queue.TryNext()) {
					OnPlaybackEnded();
					return R.Ok;
				}

				return TryPlay(false);
			}
		}

		public E<LocalStr> Previous() {
			lock (Lock) {
				Log.Info("Previous song requested");
				return TryPrevious(true);
			}
		}

		private E<LocalStr> TryPrevious(bool noSongIsError) {
			TryStopCurrentSong();
			if (!Queue.TryPrevious())
				return NoSongToPlay(noSongIsError);

			var item = Queue.Current;
			var res = Start(item);
			if (res.Ok)
				return R.Ok;

			Log.Info("Could not play song {0} (reason: {1})", item.AudioResource, res.Error);
			if (!Queue.TryNext())
				return new LocalStr("This should not happen");
			res = TryPlay(true);
			if (res.Ok) {
				return new LocalStr("Could not play previous song, playing current song again");
			} else {
				return res;
			}
		}

		public void OnPlaybackEnded() {
			Log.Info("Playback ended for some reason");
			PlaybackStopped?.Invoke(this, EventArgs.Empty);
		}

		private void TryStopCurrentSong() {
			if (CurrentPlayData != null) {
				Log.Debug("Stopping current song");
				playerConnection.Stop();
				CurrentPlayData = null;
				ResourceStopped?.Invoke(this, new SongEndEventArgs(true));
			}
		}

		// Try to start playing if not playing
		private E<LocalStr> TryInitialStart(bool noSongIsError) {
			if (IsPlaying || !AutoStartPlaying)
				return R.Ok;
			return TryPlay(noSongIsError);
		}

		private E<LocalStr> NoSongToPlay(bool noSongIsError) {
			OnPlaybackEnded();
			if(noSongIsError)
				return new LocalStr("No song to play");
			return R.Ok;
		}

		private E<LocalStr> TryPlay(bool noSongIsError) {
			while (true) {
				var item = Queue.Current;
				if (item == null)
					return NoSongToPlay(noSongIsError);

				var res = Start(item);
				if (res.Ok)
					return R.Ok;

				Log.Info("Could not play song {0} (reason: {1})", item.AudioResource, res.Error);

				if (!Queue.TryNext())
					return NoSongToPlay(noSongIsError);
			}
		}

		private E<LocalStr> Start(QueueItem item) {
			Log.Info("Starting song {0}...", item.AudioResource.ResourceTitle);

			Stopwatch timer = new Stopwatch();
			timer.Start();
			var res = songAnalyzer.TryGetResult(item);
			if (!res.Ok)
				return res.Error;

			var result = res.Value;
			
			if (item.AudioResource.ResourceTitle != result.Resource.BaseData.ResourceTitle)
			{
				// Title changed, Log that name change
				Log.Info("Title of song '{0}' changed from '{1}' to '{2}'.",
					item.MetaData.ContainingPlaylistId != null ? "in playlist '" + item.MetaData.ContainingPlaylistId + "'" : "",
					item.AudioResource.ResourceTitle,
					result.Resource.BaseData.ResourceTitle);
			}

			result.Resource.Meta = item.MetaData;
			var r = Start(result.Resource, result.Gain, result.RestoredLink.OkOr(null));
			Log.Debug("Start song took {0}ms", timer.ElapsedMilliseconds);
			return r;
		}

		private E<LocalStr> Start(PlayResource resource, int gain, string restoredLink) {
			Log.Trace("Starting resource...");
			
			var playInfo = new PlayInfoEventArgs(resource.Meta.ResourceOwnerUid, resource, restoredLink);
			BeforeResourceStarted?.Invoke(this, playInfo);
			if (string.IsNullOrWhiteSpace(resource.PlayUri)) {
				Log.Error("Internal resource error: link is empty (resource:{0})", resource);
				return new LocalStr(strings.error_playmgr_internal_error);
			}
			
			Log.Debug("AudioResource start: {0} with gain {1}", resource, gain);
			var result = playerConnection.Play(resource, gain);

			if (!result) {
				Log.Error("Error return from player: {0}", result.Error);
				return new LocalStr(strings.error_playmgr_internal_error);
			}

			playerConnection.Volume =
				Tools.Clamp(playerConnection.Volume, confBot.Audio.Volume.Min, confBot.Audio.Volume.Max);
			CurrentPlayData = playInfo; // TODO meta as readonly
			AfterResourceStarted?.Invoke(this, playInfo);
			UpdateNextSong();

			return R.Ok;
		}

		private void UpdateNextSong() {
			var next = Queue.Next;
			if (next != null)
				PrepareNextSong(next);
		}

		public void PrepareNextSong(QueueItem item) {
			lock (Lock) {
				if (songAnalyzer.IsPreparing(item))
					return;

				songAnalyzer.SetNextSong(item);

				if (playerConnection.FfmpegProducer.Length != TimeSpan.Zero) {
					Log.Info("Preparing song analyzer... (PrepareNextSong)");
					songAnalyzer.Prepare(GetAnalyzeTaskStartTime());
				}
			}
		}

		public void SongEndedEvent(object sender, EventArgs e) { StopSong(false); }

		public void Stop() {
			StopSong(true);
			songAnalyzer.Clear();
		}

		private void StopSong(bool stopped /* true if stopped manually, false if ended normally */) {
			lock (Lock) {
				Log.Debug("Song stopped");
				ResourceStopped?.Invoke(this, new SongEndEventArgs(stopped));

				if (stopped) {
					playerConnection.Stop();

					TryStopCurrentSong();
				} else {
					var result = Next();
					if (result.Ok)
						return;
					Log.Info("Automatically playing next song ended with error: {0}", result.Error);
				}
			}
		}

		public void Update(SongInfoChanged newInfo) {
			lock (Lock) {
				Log.Info("Song info (title) updated");
				var data = CurrentPlayData;
				if (data is null)
					return;
				if (newInfo.Title != null)
					data.ResourceData = data.ResourceData.WithTitle(newInfo.Title);
				// further properties...
				OnResourceUpdated?.Invoke(this, data);
			}
		}

		public static TimeSpan? ParseStartTime(string[] attrs) {
			TimeSpan? res = null;
			if (attrs != null && attrs.Length != 0) {
				foreach (var attr in attrs) {
					if (attr.StartsWith("@")) {
						res = TextUtil.ParseTime(attr.Substring(1));
					}
				}
			}

			return res;
		}
	}
}
