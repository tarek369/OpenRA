#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.IO;
using System.Threading;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using OpenRA.Platforms.Android;

// OpenRA downloads freeware game content from official mirrors (openra.net) on first run,
// so the app needs network access. Declared at assembly level so .NET Android merges it
// into the final AndroidManifest.xml.
[assembly: UsesPermission(Android.Manifest.Permission.Internet)]
[assembly: UsesPermission(Android.Manifest.Permission.AccessNetworkState)]

namespace OpenRA.Android
{
	[Activity(
		Label = "OpenRA",
		MainLauncher = true,
		Theme = "@android:style/Theme.DeviceDefault.NoActionBar.Fullscreen",
		ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.KeyboardHidden | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout,
		ScreenOrientation = ScreenOrientation.Landscape)]
	public class MainActivity : Activity
	{
		const string Tag = "OpenRA";
		OpenRASurfaceView surfaceView;
		AndroidPlatformWindow window;

		string engineDir;
		string supportDir;
		volatile bool engineStarted;

		protected override void OnCreate(Bundle savedInstanceState)
		{
			base.OnCreate(savedInstanceState);

			// Extract engine assets (glsl/, mods/, VERSION) from the APK to internal storage on first
			// launch, then point the engine at that directory. Subsequent launches skip the extraction.
			engineDir = ExtractAssets();

			// Scoped-storage-friendly support dir for user data (settings, maps, logs, content).
			supportDir = Path.Combine(GetExternalFilesDir(null).AbsolutePath, "Support") + Path.DirectorySeparatorChar;
			Directory.CreateDirectory(supportDir);

			var metrics = Resources.DisplayMetrics;
			window = new AndroidPlatformWindow(metrics.WidthPixels, metrics.HeightPixels);
			AndroidPlatform.SetWindow(window);

			surfaceView = new OpenRASurfaceView(this, window);
			window.HostView = surfaceView;
			window.KeyboardDrainAction = ih => surfaceView.DrainKeyboardInput(ih);
			SetContentView(surfaceView);

			// Start the engine loop immediately. The window's WaitForSurfaceAndInitializeGl handles
			// the Android surface churn (create->destroy->create during layout) by retrying.
			StartEngineOnce();
		}

		// Called from the SurfaceCallback once the first stable surface is available.
		internal void StartEngineOnce()
		{
			if (engineStarted)
				return;
			engineStarted = true;

			var args = new[]
			{
				$"Engine.EngineDir={engineDir}",
				$"Engine.SupportDir={supportDir}",
				"Game.Mod=ra"
			};

			new Thread(() =>
			{
				try
				{
					Game.InitializeAndRun(args);
				}
				catch (Exception e)
				{
					global::Android.Util.Log.Error(Tag, $"OpenRA crashed: {e}");
					RunOnUiThread(() => Toast.MakeText(this, $"OpenRA crashed: {e.Message}", ToastLength.Long)?.Show());
				}
			})
			{ Name = "OpenRA Main", IsBackground = false }.Start();
		}

		string ExtractAssets()
		{
			var dest = Path.Combine(FilesDir.AbsolutePath, "engine") + Path.DirectorySeparatorChar;
			var marker = Path.Combine(dest, ".extracted");

			if (File.Exists(marker))
				return dest;

			Directory.CreateDirectory(dest);
			CopyAssetDir("glsl", Path.Combine(dest, "glsl"));
			CopyAssetDir("mods", Path.Combine(dest, "mods"));
			CopyAssetFile("VERSION", Path.Combine(dest, "VERSION"));
			CopyAssetFile("global mix database.dat", Path.Combine(dest, "global mix database.dat"));

			// The map directories are excluded from the APK assets (maps are large and not needed
			// for the menu), but MapCache.LoadMaps expects each mod's maps/ folder to exist.
			foreach (var mod in new[] { "ra", "cnc", "d2k", "ts", "all", "common" })
				Directory.CreateDirectory(Path.Combine(dest, "mods", mod, "maps"));

			File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
			global::Android.Util.Log.Info(Tag, $"Extracted engine assets to {dest}");
			return dest;
		}

		void CopyAssetDir(string assetPath, string destDir)
		{
			Directory.CreateDirectory(destDir);
			string[] children;
			try { children = Assets.List(assetPath); }
			catch { return; }

			if (children == null)
				return;

			foreach (var child in children)
			{
				var childAsset = $"{assetPath}/{child}";
				var childDest = Path.Combine(destDir, child);

				// Recurse into directories.
				string[] grandChildren = null;
				try { grandChildren = Assets.List(childAsset); }
				catch { }

				if (grandChildren != null && grandChildren.Length > 0)
					CopyAssetDir(childAsset, childDest);
				else
					CopyAssetFile(childAsset, childDest);
			}
		}

		void CopyAssetFile(string assetPath, string destFile)
		{
			try
			{
				using var input = Assets.Open(assetPath);
				using var output = File.Create(destFile);
				input.CopyTo(output);
			}
			catch (Java.IO.IOException)
			{
				// Some asset entries (e.g. empty dirs) may fail; skip.
			}
		}

		// SurfaceView that owns the Android window surface and forwards touch + keyboard input.
		sealed class OpenRASurfaceView : SurfaceView
		{
			readonly AndroidPlatformWindow window;

			// Keep a strong reference to the callback: Java's AddCallback holds only a weak/global
			// ref, so a purely temporary instance would be collected by the .NET GC and stop firing.
			readonly SurfaceCallback callback;

			readonly MainActivity activity;

			// Buffer for IME-composed text, drained by PumpInput on the game thread.
			readonly System.Collections.Concurrent.ConcurrentQueue<string> textQueue = new();
			readonly System.Collections.Concurrent.ConcurrentQueue<KeyInput> keyQueue = new();

			public OpenRASurfaceView(MainActivity context, AndroidPlatformWindow window)
				: base(context)
			{
				this.window = window;
				this.activity = context;
				callback = new SurfaceCallback(window, context);
				Holder.AddCallback(callback);
				Focusable = true;
				FocusableInTouchMode = true;
			}

			public override bool OnTouchEvent(MotionEvent e)
			{
				window.EnqueueMotion(e);
				return true;
			}

			// Capture hardware keyboard events (also some IME key events like backspace).
			public override bool DispatchKeyEvent(KeyEvent e)
			{
				// Let the IME handle composition first.
				if (base.DispatchKeyEvent(e))
					return true;

				if (e.Action == KeyEventActions.Down || e.Action == KeyEventActions.Up)
				{
					var ki = new KeyInput
					{
						Event = e.Action == KeyEventActions.Down ? KeyInputEvent.Down : KeyInputEvent.Up,
						Key = MapKeycode(e.KeyCode),
						UnicodeChar = (char)e.UnicodeChar,
						Modifiers = Modifiers.None,
						IsRepeat = e.RepeatCount > 0
					};
					keyQueue.Enqueue(ki);

					// If this key produced a printable character, also send it as text.
					if (e.Action == KeyEventActions.Down && ki.UnicodeChar != 0 && !char.IsControl(ki.UnicodeChar))
						textQueue.Enqueue(ki.UnicodeChar.ToString());
				}

				return true;
			}

			// Provide an InputConnection so the Android IME (soft keyboard) can send composed text.
			public override global::Android.Views.InputMethods.IInputConnection OnCreateInputConnection(global::Android.Views.InputMethods.EditorInfo outAttrs)
			{
				outAttrs.InputType = global::Android.Text.InputTypes.ClassText;
				outAttrs.ImeOptions = (global::Android.Views.InputMethods.ImeFlags)global::Android.Views.InputMethods.ImeAction.None;
				return new OpenRAInputConnection(this, true);
			}

			// Called by PumpInput on the game thread to drain queued text/key events.
			public void DrainKeyboardInput(IInputHandler inputHandler)
			{
				while (keyQueue.TryDequeue(out var ki))
					inputHandler.OnKeyInput(ki);
				while (textQueue.TryDequeue(out var text))
					inputHandler.OnTextInput(text);
			}

			static OpenRA.Keycode MapKeycode(global::Android.Views.Keycode kc)
			{
				// Map common Android keycodes to OpenRA's Keycode enum (which mirrors SDL2).
				// Uses integer comparison to avoid enum-naming differences across .NET Android versions.
				var v = (int)kc;
				return v switch
				{
					66 => OpenRA.Keycode.RETURN,     // KEYCODE_ENTER
					67 => OpenRA.Keycode.BACKSPACE,  // KEYCODE_DEL
					61 => OpenRA.Keycode.TAB,        // KEYCODE_TAB
					111 => OpenRA.Keycode.ESCAPE,    // KEYCODE_ESCAPE
					62 => OpenRA.Keycode.SPACE,      // KEYCODE_SPACE
					17 => OpenRA.Keycode.LEFT,       // KEYCODE_DPAD_LEFT
					22 => OpenRA.Keycode.RIGHT,      // KEYCODE_DPAD_RIGHT
					19 => OpenRA.Keycode.UP,         // KEYCODE_DPAD_UP
					20 => OpenRA.Keycode.DOWN,       // KEYCODE_DPAD_DOWN
					_ => OpenRA.Keycode.UNKNOWN
				};
			}

			// A minimal InputConnection that captures IME text commit.
			sealed class OpenRAInputConnection : global::Android.Views.InputMethods.BaseInputConnection
			{
				readonly OpenRASurfaceView view;

				public OpenRAInputConnection(OpenRASurfaceView targetView, bool fullEditor)
					: base(targetView, fullEditor)
				{
					view = targetView;
				}

				public override bool CommitText(Java.Lang.ICharSequence text, int newCursorPosition)
				{
					if (text != null && text.Length() > 0)
						view.textQueue.Enqueue(text.ToString());
					return true;
				}

				public override bool DeleteSurroundingText(int beforeLength, int afterLength)
				{
					// Map IME delete to backspace key events.
					for (var i = 0; i < beforeLength; i++)
						view.keyQueue.Enqueue(new KeyInput { Event = KeyInputEvent.Down, Key = OpenRA.Keycode.BACKSPACE });
					return true;
				}
			}
		}

		sealed class SurfaceCallback : Java.Lang.Object, ISurfaceHolderCallback
		{
			readonly AndroidPlatformWindow window;
			readonly MainActivity activity;

			public SurfaceCallback(AndroidPlatformWindow window, MainActivity activity)
			{
				this.window = window;
				this.activity = activity;
			}

			public void SurfaceCreated(ISurfaceHolder holder)
			{
				window.NotifySurfaceReady(holder);
				activity.StartEngineOnce();
			}

			public void SurfaceChanged(ISurfaceHolder holder, global::Android.Graphics.Format format, int width, int height) => window.NotifySurfaceReady(holder);
			public void SurfaceDestroyed(ISurfaceHolder holder) => window.NotifySurfaceDestroyed();
		}
	}
}
