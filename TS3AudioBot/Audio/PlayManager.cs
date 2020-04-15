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
using System.Linq;
using TS3AudioBot.Config;
using TS3AudioBot.Environment;
using TS3AudioBot.Helper;
using TS3AudioBot.Localization;
using TS3AudioBot.Playlists;
using TS3AudioBot.ResourceFactories;
using TSLib;
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

		public PlayManager(ConfBot config, Player playerConnection, ResolveContext resourceResolver, Stats stats) {
			confBot = config;
			this.playerConnection = playerConnection;
			this.resourceResolver = resourceResolver;
			this.stats = stats;
		}

		public void Clear() {
			lock (Lock) {
				Queue.Clear();
				TryStopCurrentSong();
				OnPlaybackEnded();
			}
		}

		public E<LocalStr> Play() {
			lock (Lock) {
				return TryPlay();
			}
		}

		public void EnqueueAsNextSong(QueueItem item) {
			lock (Lock) {
				Queue.InsertAfter(item, Queue.Index);
			}
		}

		public void RemoveAt(int index) {
			lock (Lock) {
				Queue.Remove(index);
			}
		}

		public void RemoveRange(int from, int to) {
			lock (Lock) {
				Queue.RemoveRange(from, to);
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
				return TryInitialStart();
			}
		}

		public E<LocalStr> Enqueue(IEnumerable<QueueItem> items) {
			lock (Lock) {
				Queue.Enqueue(items);
				return TryInitialStart();
			}
		}

		public E<LocalStr> Next(int count = 1) {
			if (count <= 0)
				return R.Ok;
			lock (Lock) {
				Log.Info("Skip {0} songs requested", count);
				TryStopCurrentSong();
				Queue.Skip(count);
				return TryPlay();
			}
		}

		public E<LocalStr> Next() {
			lock (Lock) {
				Log.Info("Next song requested");
				TryStopCurrentSong();
				if (!Queue.TryNext()) {
					OnPlaybackEnded();
					return new LocalStr("No song to play");
				}

				return TryPlay();
			}
		}

		public E<LocalStr> Previous() {
			lock (Lock) {
				Log.Info("Previous song requested");
				return TryPrevious();
			}
		}

		private E<LocalStr> TryPrevious() {
			TryStopCurrentSong();
			if (!Queue.TryPrevious())
				return new LocalStr("No song to play");

			var item = Queue.Current;
			var res = Start(item);
			if (res.Ok)
				return R.Ok;

			Log.Info("Could not play song {0} (reason: {1})", item.AudioResource, res.Error);
			if (!Queue.TryNext())
				return new LocalStr("This should not happen");
			res = TryPlay();
			if (res.Ok) {
				return new LocalStr("Could not play previous song, playing current song again");
			} else {
				OnPlaybackEnded();
				return res;
			}
		}

		public void OnPlaybackEnded() {
			Log.Info("Playback ended for some reason");
			PlaybackStopped?.Invoke(this, EventArgs.Empty);
		}

		private void TryStopCurrentSong() {
			if (CurrentPlayData != null) {
				Log.Info("Stopping current song");
				playerConnection.Stop();
				CurrentPlayData = null;
				ResourceStopped?.Invoke(this, new SongEndEventArgs(true));
			}
		}

		// Try to start playing if not playing
		private E<LocalStr> TryInitialStart() {
			if (IsPlaying || !AutoStartPlaying)
				return R.Ok;
			return TryPlay();
		}

		private E<LocalStr> TryPlay() {
			while (true) {
				var item = Queue.Current;
				if (item == null) {
					OnPlaybackEnded();
					return new LocalStr("No song to play");
				}

				var res = Start(item);
				if (res.Ok)
					return R.Ok;

				Log.Info("Could not play song {0} (reason: {1})", item.AudioResource, res.Error);

				if (!Queue.TryNext()) {
					OnPlaybackEnded();
					return new LocalStr("No song to play");
				}
			}
		}

		private E<LocalStr> Start(QueueItem item) {
			Log.Info("Starting song...");
			var resource = resourceResolver.Load(item.AudioResource);
			if (!resource.Ok)
				return resource.Error;

			if (item.AudioResource.ResourceTitle != resource.Value.BaseData.ResourceTitle)
			{
				// Title changed, Log that name change
				Log.Info("Title of song {0} changed from '{1}' to '{2}'.",
					item.MetaData.ContainingPlaylistId != null ? "in playlist '" + item.MetaData.ContainingPlaylistId + "'" : "",
					item.AudioResource.ResourceTitle,
					resource.Value.BaseData.ResourceTitle);
			}

			return Start(resource.Value, item.MetaData);
		}

		private E<LocalStr> Start(PlayResource resource, MetaData meta) {
			Log.Info("Starting resource...");
			resource.Meta = meta;
			var sourceLink = resourceResolver.RestoreLink(resource.BaseData).OkOr(null);
			var playInfo = new PlayInfoEventArgs(meta.ResourceOwnerUid, resource, sourceLink);
			BeforeResourceStarted?.Invoke(this, playInfo);
			if (string.IsNullOrWhiteSpace(resource.PlayUri)) {
				Log.Error("Internal resource error: link is empty (resource:{0})", resource);
				return new LocalStr(strings.error_playmgr_internal_error);
			}

			Log.Debug("AudioResource start: {0}", resource);
			var result = playerConnection.Play(resource);

			if (!result) {
				Log.Error("Error return from player: {0}", result.Error);
				return new LocalStr(strings.error_playmgr_internal_error);
			}

			playerConnection.Volume =
				Tools.Clamp(playerConnection.Volume, confBot.Audio.Volume.Min, confBot.Audio.Volume.Max);
			CurrentPlayData = playInfo; // TODO meta as readonly
			AfterResourceStarted?.Invoke(this, playInfo);

			return R.Ok;
		}

		public void SongEndedEvent(object sender, EventArgs e) { StopSong(false); }

		public void Stop() { StopSong(true); }

		private void StopSong(bool stopped /* true if stopped manually, false if ended normally */) {
			lock (Lock) {
				Log.Info("Song stopped");
				ResourceStopped?.Invoke(this, new SongEndEventArgs(stopped));

				if (stopped) {
					playerConnection.Stop();

					TryStopCurrentSong();
				} else {
					var result = Next();
					if (result.Ok)
						return;
					Log.Info("Song items ended with error: {0}", result.Error);
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
					data.ResourceData.ResourceTitle = newInfo.Title;
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
