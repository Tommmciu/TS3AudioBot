namespace TS3AudioBot
{
	using System;
	using TS3AudioBot.Helper;

	public sealed class AudioFramework : MarshalByRefObject, IDisposable
	{
		public int MaxUserVolume => audioFrameworkData.maxUserVolume;
		public const int MaxVolume = 100;
		private static readonly TimeSpan SongEndTimeout = TimeSpan.FromSeconds(30);
		private static readonly TimeSpan SongEndTimeoutInterval = TimeSpan.FromSeconds(1);

		private AudioFrameworkData audioFrameworkData;
		private TickWorker waitEndTick;
		private DateTime endTime;

		public PlayData CurrentPlayData { get; private set; }
		private IPlayerConnection playerConnection;
		public PlaylistManager PlaylistManager { get; }

		public event EventHandler<PlayData> OnResourceStarted;
		public event EventHandler<bool> OnResourceStopped;

		// Playerproperties

		public bool IsPlaying => CurrentPlayData != null;
		/// <summary>Loop state for the entire playlist.</summary>
		public bool Loop { get { return PlaylistManager.Loop; } set { PlaylistManager.Loop = value; } }
		/// <summary>Loop state for the current song.</summary>
		public bool Repeat { get { return playerConnection.Repeated; } set { playerConnection.Repeated = value; } }
		public int Volume { get { return playerConnection.Volume; } set { playerConnection.Volume = value; } }
		/// <summary>Starts or resumes the current song.</summary>
		public bool Pause { get { return playerConnection.Pause; } set { playerConnection.Pause = value; } }

		// Playermethods

		/// <summary>Jumps to the position in the audiostream if available.</summary>
		/// <param name="pos">Position in seconds from the start.</param>
		/// <returns>True if the seek request was valid, false otherwise.</returns>
		public bool Seek(TimeSpan pos)
		{
			if (pos < TimeSpan.Zero || pos > playerConnection.Length)
				return false;
			playerConnection.Position = pos;
			return true;
		}

		/// <summary>Plays the next song in the playlist or queue.</summary>
		public void Next()
		{
			var next = PlaylistManager.Next();
			if (next != null)
			{
				CurrentPlayData = next;
				StartResource(CurrentPlayData);
			}
			else
			{
				Stop(false);
			}
		}

		/// <summary>Plays the previous song in the playlist.</summary>
		public void Previous()
		{
			// TODO via history ?
		}

		private void OnSongEnd() => Next();

		// Audioframework

		/// <summary>Creates a new AudioFramework</summary>
		/// <param name="afd">Required initialization data from a ConfigFile interpreter.</param>
		internal AudioFramework(AudioFrameworkData afd, IPlayerConnection audioBackEnd, PlaylistManager playlistMgr)
		{
			if (audioBackEnd == null)
				throw new ArgumentNullException(nameof(audioBackEnd));

			if (audioBackEnd.SupportsEndCallback)
				audioBackEnd.OnSongEnd += (s, e) => OnSongEnd();
			else
				waitEndTick = TickPool.RegisterTick(NotifyEnd, SongEndTimeoutInterval, false);

			audioFrameworkData = afd;
			playerConnection = audioBackEnd;
			playerConnection.Initialize();

			PlaylistManager = playlistMgr;
		}

		/// <summary>
		/// <para>Gets started at the beginning of a new resource.</para>
		/// <para>It calls the stop event when a resource is finished.</para>
		/// <para>Is used for player backends which are not supporting an end callback.</para>
		/// </summary>
		private void NotifyEnd()
		{
			if (endTime < Util.GetNow())
			{
				if (playerConnection.IsPlaying)
				{
					var playtime = playerConnection.Length;
					var position = playerConnection.Position;

					var endspan = playtime - position;
					endTime = Util.GetNow().Add(endspan);
				}
				else if (endTime + SongEndTimeout < Util.GetNow())
				{
					Log.Write(Log.Level.Debug, "AF Song ended with default timeout");
					OnSongEnd();
					waitEndTick.Active = false;
				}
			}
		}

		/// <summary>
		/// <para>Do NOT call this method directly! Use the FactoryManager instead.</para>
		/// <para>Stops the old resource and starts the new one.</para>
		/// <para>The volume gets resetted and the OnStartEvent gets triggered.</para>
		/// </summary>
		/// <param name="playData">The info struct containing the PlayResource to start.</param>
		internal R StartResource(PlayData playData)
		{
			if (playData?.PlayResource == null)
			{
				Log.Write(Log.Level.Debug, "AF audioResource is null");
				return "No new resource";
			}

			Stop(true);

			string resourceLink = playData.PlayResource.Play();
			if (string.IsNullOrWhiteSpace(resourceLink))
				return "Internal resource error: link is empty";

			Log.Write(Log.Level.Debug, "AF ar start: {0}", playData.ResourceData);
			playerConnection.AudioStart(resourceLink);

			Volume = playData.Volume ?? audioFrameworkData.defaultVolume;
			Log.Write(Log.Level.Debug, "AF set volume: {0}", Volume);

			OnResourceStarted?.Invoke(this, playData);
			CurrentPlayData = playData;

			if (!playerConnection.SupportsEndCallback)
			{
				endTime = Util.GetNow();
				waitEndTick.Active = true;
			}
			return R.OkR;
		}

		public void Stop()
		{
			Stop(false);
		}

		/// <summary>Stops the currently played song.</summary>
		/// <param name="restart">When set to true, the AudioBob won't be notified aubout the stop.
		/// Use this parameter to prevent fast off-on switching.</param>
		private void Stop(bool restart)
		{
			Log.Write(Log.Level.Debug, "AF stop old (restart:{0})", restart);
			if (CurrentPlayData != null)
			{
				CurrentPlayData = null;
				if (!restart)
					playerConnection.AudioStop();
				OnResourceStopped?.Invoke(this, restart);
			}
		}

		public void Dispose()
		{
			Log.Write(Log.Level.Info, "Closing Mediaplayer...");

			Stop(false);

			if (playerConnection != null)
			{
				playerConnection.Dispose();
				playerConnection = null;
				Log.Write(Log.Level.Debug, "AF playerConnection disposed");
			}
		}
	}

	public struct AudioFrameworkData
	{
		//[InfoAttribute("the absolute or relative path to the local music folder")]
		//public string localAudioPath;
		[Info("the default volume a song should start with")]
		public int defaultVolume;
		[Info("the maximum volume a normal user can request")]
		public int maxUserVolume;
		[Info("the location of the vlc player (if the vlc backend is used)", "vlc")]
		public string vlcLocation;
	}

	public enum AudioType
	{
		MediaLink,
		Youtube,
		Soundcloud,
		Twitch,
	}
}
