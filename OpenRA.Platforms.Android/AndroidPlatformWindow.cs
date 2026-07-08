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
using System.Threading;
using Android.Runtime;
using Android.Views;
using OpenRA;
using OpenRA.Primitives;

namespace OpenRA.Platforms.Android
{
	// IPlatformWindow backed by an Android SurfaceView + EGL. The Activity hosts the
	// OpenRASurfaceView and hands its ISurfaceHolder here via SurfaceCreated/SurfaceChanged.
	// EGL display/context/surface lifecycles follow the holder callbacks. GL runs on the game
	// thread (the thread that runs Game.Loop), which calls Context.InitializeOpenGL via
	// OnContextReady the first time a surface is available.
	public sealed class AndroidPlatformWindow : ThreadAffine, IPlatformWindow
	{
		AndroidGraphicsContext context;
		readonly AndroidInput input;

		IntPtr eglDisplay = Egl.EGL_NO_DISPLAY;
		IntPtr eglContext = Egl.EGL_NO_CONTEXT;
		IntPtr eglSurface = Egl.EGL_NO_SURFACE;
		IntPtr eglConfig;

		// True once EGL + surface are created and GL has been initialized on the game thread.
		volatile bool contextReady;
		volatile bool surfaceAvailable;
		Size surfaceSize;

		// DPI: Android exposes density. We treat EffectiveWindowScale == 1 for Phase 0 and let
		// the engine scale UI from its own settings; surface/native sizes are reported in pixels.
		float nativeScale = 1f;

		public Size NativeWindowSize { get; private set; }
		public Size EffectiveWindowSize { get; private set; }
		public float NativeWindowScale => nativeScale;
		public float EffectiveWindowScale => nativeScale;
		public Size SurfaceSize => surfaceSize;
		public int DisplayCount => 1;
		public int CurrentDisplay => 0;
		public bool HasInputFocus => true;
		public bool IsSuspended => !surfaceAvailable;

		public event Action<float, float, float, float> OnWindowScaleChanged;

		public IntPtr Display => eglDisplay;
		public IntPtr Surface => eglSurface;
		public IntPtr ContextPtr => eglContext;

		// True only when there's a live EGL surface to swap against. Present() consults this to
		// avoid spamming EGL errors when the Android surface is in transition.
		public bool SurfaceValid => eglSurface != Egl.EGL_NO_SURFACE;

		// Mark the EGL surface as needing recreation (called when a swap reports a bad surface).
		public void InvalidateSurface() { surfaceNeedsRecreate = true; }

		public IGraphicsContext Context => context;

		public GLProfile GLProfile => OpenRA.GLProfile.Embedded;
		public GLProfile[] SupportedGLProfiles => new[] { OpenRA.GLProfile.Embedded };

		public AndroidPlatformWindow(int width, int height)
		{
			NativeWindowSize = EffectiveWindowSize = surfaceSize = new Size(width, height);
			input = new AndroidInput();
		}

		// The most recent holder handed to us by the UI thread, plus the dimensions it reported.
		// EGL setup is performed entirely on the game thread (WaitForSurfaceAndInitializeGl) to
		// avoid cross-thread surface/context races, which on Android cause EGL_BAD_SURFACE on swap.
		global::Android.Views.ISurfaceHolder pendingHolder;

		// Set when a new holder has arrived and the EGL surface needs (re)creation on the game thread.
		volatile bool surfaceNeedsRecreate;

		// Called by the Activity's ISurfaceHolderCallback when the surface is created/changed.
		public void NotifySurfaceReady(global::Android.Views.ISurfaceHolder holder)
		{
			var frame = holder.SurfaceFrame;
			int w = frame.Width() > 0 ? frame.Width() : 0;
			int h = frame.Height() > 0 ? frame.Height() : 0;

			lock (surfaceGate)
			{
				if (w > 0 && h > 0)
				{
					surfaceSize = new Size(w, h);
					NativeWindowSize = EffectiveWindowSize = surfaceSize;
				}

				pendingHolder = holder;

				// Do EGL setup eagerly here on the UI thread. Creating the EGL surface against the
				// freshly-created ANativeWindow keeps Android from destroying it during initial layout
				// churn — once EGL owns it, the surface stays live. GL make-current/load happens later
				// on the game thread in InitializeOpenGL (the context is portable across threads).
				try
				{
					if (eglDisplay == Egl.EGL_NO_DISPLAY)
						CreateEglContext(holder);

					if (eglSurface == Egl.EGL_NO_SURFACE)
						CreateEglSurface(holder);

					surfaceAvailable = true;
					surfaceNeedsRecreate = false;
				}
				catch (Exception e)
				{
					global::Android.Util.Log.Error("OpenRA", $"NotifySurfaceReady EGL setup failed: {e}");
				}

				Monitor.PulseAll(surfaceGate);
			}
		}

		// Called by the Activity's ISurfaceHolderCallback when the surface is destroyed.
		public void NotifySurfaceDestroyed()
		{
			// With eager EGL setup we intentionally keep the EGL surface across Android's surface
			// churn: once EGL has created a window surface from the ANativeWindow, the underlying
			// gralloc buffer stays valid for the render loop. If the surface genuinely dies, the
			// swap in Present reports EGL_BAD_SURFACE and InvalidateSurface schedules a clean
			// recreate on the next NotifySurfaceReady. So this is a no-op for Phase 0.
		}

		// Called on the game thread before any GL work (Present). Recreates the EGL surface if the
		// Android surface was regenerated, and tears it down if destroyed. This keeps all EGL
		// surface lifecycle on the game thread, matching the EGL context's thread affinity.
		internal void EnsureCurrentSurface()
		{
			// Nothing to do if no recreation was requested, or if we still hold a valid EGL surface
			// (the eager-create model keeps the surface alive across churn).
			if (!surfaceNeedsRecreate || eglSurface != Egl.EGL_NO_SURFACE)
			{
				surfaceNeedsRecreate = false;
				return;
			}

			surfaceNeedsRecreate = false;

			// Tear down any existing EGL surface first.
			if (eglDisplay != Egl.EGL_NO_DISPLAY)
			{
				if (eglSurface != Egl.EGL_NO_SURFACE)
				{
					Egl.eglMakeCurrent(eglDisplay, Egl.EGL_NO_SURFACE, Egl.EGL_NO_SURFACE, Egl.EGL_NO_CONTEXT);
					Egl.eglDestroySurface(eglDisplay, eglSurface);
					eglSurface = Egl.EGL_NO_SURFACE;
				}
			}

			// If a new Android surface is available, build a new EGL surface for it.
			global::Android.Views.ISurfaceHolder holder;
			lock (surfaceGate)
				holder = pendingHolder;

			if (surfaceAvailable && holder != null && eglDisplay != Egl.EGL_NO_DISPLAY)
			{
				try { CreateEglSurface(holder); }
				catch (Exception e) { global::Android.Util.Log.Error("OpenRA", $"CreateEglSurface on recreate: {e}"); }
			}
		}

		void CreateEglContext(global::Android.Views.ISurfaceHolder holder)
		{
			eglDisplay = Egl.eglGetDisplay(new IntPtr(Egl.EGL_DEFAULT_DISPLAY));
			if (eglDisplay == Egl.EGL_NO_DISPLAY)
				throw new InvalidOperationException("eglGetDisplay failed");

			if (!Egl.eglInitialize(eglDisplay, out _, out _))
				throw new InvalidOperationException("eglInitialize failed");

			// Try a sequence of progressively relaxed attribute sets. Drivers vary in which
			// configs they expose (alpha, exact depth size, GLES3 vs GLES2), so the canonical
			// Android pattern is to attempt the strictest match first and fall back.
			var configCandidates = new[]
			{
				new[] { Egl.EGL_RED_SIZE, 8, Egl.EGL_GREEN_SIZE, 8, Egl.EGL_BLUE_SIZE, 8, Egl.EGL_ALPHA_SIZE, 8, Egl.EGL_DEPTH_SIZE, 16, Egl.EGL_RENDERABLE_TYPE, Egl.EGL_OPENGL_ES3_BIT, Egl.EGL_SURFACE_TYPE, Egl.EGL_WINDOW_BIT, Egl.EGL_NONE },
				new[] { Egl.EGL_RED_SIZE, 8, Egl.EGL_GREEN_SIZE, 8, Egl.EGL_BLUE_SIZE, 8, Egl.EGL_DEPTH_SIZE, 16, Egl.EGL_RENDERABLE_TYPE, Egl.EGL_OPENGL_ES3_BIT, Egl.EGL_SURFACE_TYPE, Egl.EGL_WINDOW_BIT, Egl.EGL_NONE },
				new[] { Egl.EGL_RED_SIZE, 5, Egl.EGL_GREEN_SIZE, 6, Egl.EGL_BLUE_SIZE, 5, Egl.EGL_DEPTH_SIZE, 16, Egl.EGL_RENDERABLE_TYPE, Egl.EGL_OPENGL_ES3_BIT, Egl.EGL_SURFACE_TYPE, Egl.EGL_WINDOW_BIT, Egl.EGL_NONE },
				new[] { Egl.EGL_RED_SIZE, 8, Egl.EGL_GREEN_SIZE, 8, Egl.EGL_BLUE_SIZE, 8, Egl.EGL_ALPHA_SIZE, 8, Egl.EGL_DEPTH_SIZE, 16, Egl.EGL_RENDERABLE_TYPE, Egl.EGL_OPENGL_ES2_BIT, Egl.EGL_SURFACE_TYPE, Egl.EGL_WINDOW_BIT, Egl.EGL_NONE },
				new[] { Egl.EGL_RED_SIZE, 8, Egl.EGL_GREEN_SIZE, 8, Egl.EGL_BLUE_SIZE, 8, Egl.EGL_DEPTH_SIZE, 16, Egl.EGL_RENDERABLE_TYPE, Egl.EGL_OPENGL_ES2_BIT, Egl.EGL_SURFACE_TYPE, Egl.EGL_WINDOW_BIT, Egl.EGL_NONE },
				new[] { Egl.EGL_DEPTH_SIZE, 16, Egl.EGL_RENDERABLE_TYPE, Egl.EGL_OPENGL_ES3_BIT, Egl.EGL_SURFACE_TYPE, Egl.EGL_WINDOW_BIT, Egl.EGL_NONE },
				new[] { Egl.EGL_RENDERABLE_TYPE, Egl.EGL_OPENGL_ES3_BIT, Egl.EGL_SURFACE_TYPE, Egl.EGL_WINDOW_BIT, Egl.EGL_NONE },
				new[] { Egl.EGL_RENDERABLE_TYPE, Egl.EGL_OPENGL_ES2_BIT, Egl.EGL_SURFACE_TYPE, Egl.EGL_WINDOW_BIT, Egl.EGL_NONE }
			};

			var found = false;
			var glesMajor = 3;
			foreach (var attribs in configCandidates)
			{
				if (Egl.eglChooseConfig(eglDisplay, attribs, out eglConfig, 1, out _) && eglConfig != IntPtr.Zero)
				{
					// Record which ES version this config supports so we request a matching context.
					glesMajor = (attribs[Array.IndexOf(attribs, Egl.EGL_RENDERABLE_TYPE) + 1] == Egl.EGL_OPENGL_ES3_BIT) ? 3 : 2;
					found = true;
					break;
				}
			}

			if (!found)
			{
				var ver = Egl.QueryString(eglDisplay, Egl.EGL_VERSION);
				throw new InvalidOperationException($"eglChooseConfig found no suitable config. EGL version: {ver}");
			}

			var ctxAttribs = new int[] { Egl.EGL_CONTEXT_CLIENT_VERSION, glesMajor, Egl.EGL_NONE };
			eglContext = Egl.eglCreateContext(eglDisplay, eglConfig, Egl.EGL_NO_CONTEXT, ctxAttribs);
			if (eglContext == Egl.EGL_NO_CONTEXT)
			{
				// Retry as GLES2 context.
				ctxAttribs[1] = 2;
				eglContext = Egl.eglCreateContext(eglDisplay, eglConfig, Egl.EGL_NO_CONTEXT, ctxAttribs);
				if (eglContext == Egl.EGL_NO_CONTEXT)
					throw new InvalidOperationException($"eglCreateContext failed: EGL error 0x{Egl.eglGetError():X}");
			}
		}

		void CreateEglSurface(global::Android.Views.ISurfaceHolder holder)
		{
			// eglCreateWindowSurface needs an ANativeWindow*, not the Java Surface handle.
			// Use ANativeWindow_fromSurface to unwrap the Java object. JNIEnv.Handle is the
			// current thread's JNIEnv*; holder.Surface.Handle is the JNI local ref to the Surface.
			var surface = holder.Surface;
			var window = Egl.ANativeWindow_fromSurface(JNIEnv.Handle, surface.Handle);

			var surfaceAttribs = new int[] { Egl.EGL_NONE };
			eglSurface = Egl.eglCreateWindowSurface(eglDisplay, eglConfig, window, surfaceAttribs);
			if (eglSurface == Egl.EGL_NO_SURFACE)
				throw new InvalidOperationException($"eglCreateWindowSurface failed: EGL error 0x{Egl.eglGetError():X}");
		}

		// Called from Game.Loop's first RenderTick once the surface is available.
		public void EnsureContextInitialized()
		{
			if (contextReady || !surfaceAvailable)
				return;

			if (context == null)
				context = new AndroidGraphicsContext(this);

			context.InitializeOpenGL();
			contextReady = true;
		}

		// Blocks the calling (game) thread until the UI thread has signalled the surface is ready,
		// then performs GL initialization on this thread. The EGL context must be made current on
		// the same thread that issues GL calls, so this runs on the game thread, not the UI thread.
		readonly object surfaceGate = new();
		public void WaitForSurfaceAndInitializeGl()
		{
			global::Android.Util.Log.Info("OpenRA", $"WaitForSurface: surfaceAvailable={surfaceAvailable}, contextReady={contextReady}");

			// EGL display/context/surface are created eagerly in NotifySurfaceReady (UI thread).
			// Here we wait for a usable EGL surface, then load GL on the game thread. We key off the
			// EGL surface (the actual GL resource) rather than the Android surfaceAvailable flag, which
			// toggles during the surface churn but leaves the EGL surface intact.
			lock (surfaceGate)
			{
				var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
				while (eglSurface == Egl.EGL_NO_SURFACE && DateTime.UtcNow < deadline)
					Monitor.Wait(surfaceGate, 250);
			}

			global::Android.Util.Log.Info("OpenRA", $"WaitForSurface: eglSurface={eglSurface != Egl.EGL_NO_SURFACE}, contextReady={contextReady}");

			if (!contextReady && eglSurface != Egl.EGL_NO_SURFACE)
			{
				if (context == null)
					context = new AndroidGraphicsContext(this);

				try
				{
					context.InitializeOpenGL();
					contextReady = true;
					global::Android.Util.Log.Info("OpenRA", "InitializeOpenGL succeeded");
				}
				catch (Exception e)
				{
					global::Android.Util.Log.Error("OpenRA", $"InitializeOpenGL FAILED: {e}");
					throw;
				}
			}
		}

		// Game.Loop calls Renderer.EndFrame -> Window.PumpInput once per frame.
		public void PumpInput(IInputHandler inputHandler)
		{
			input.PumpInput(inputHandler, EffectiveWindowSize, SurfaceSize, nativeScale);

			// Drain any keyboard/IME text that the SurfaceView queued on the UI thread. The queues
			// are ConcurrentQueue so this is safe to drain from the game thread.
			KeyboardDrainAction?.Invoke(inputHandler);
		}

		// Set by the Activity so PumpInput can drain the SurfaceView's keyboard queues on the game thread.
		public Action<IInputHandler> KeyboardDrainAction;

		// Input posted from the UI thread (OpenRASurfaceView.OnTouchEvent).
		public void EnqueueMotion(MotionEvent e) => input.Enqueue(e, EffectiveWindowSize);

		public string GetClipboardText() => string.Empty;
		public bool SetClipboardText(string text) => false;
		public bool TryOpenUrl(string url) => false;

		public void GrabWindowMouseFocus() { }
		public void ReleaseWindowMouseFocus() { }

		public IHardwareCursor CreateHardwareCursor(string name, Size size, byte[] data, int2 hotspot, bool pixelDouble)
		{
			// Hardware (OS) cursors are not used on touch devices; the engine draws its own cursor sprite.
			return new NullHardwareCursor();
		}

		public void SetHardwareCursor(IHardwareCursor cursor) { }

		public void SetWindowTitle(string title) { }
		public void SetRelativeMouseMode(bool mode) { }
		public void SetScaleModifier(float scale)
		{
			nativeScale = scale;
			OnWindowScaleChanged?.Invoke(nativeScale, scale, nativeScale, scale);
		}

		// The View that hosts the SurfaceView, set by the Activity so we can toggle the IME.
		public global::Android.Views.View HostView { get; set; }

		public void StartTextInput()
		{
			// Show the Android soft keyboard via the InputMethodManager, on the UI thread.
			if (HostView == null)
				return;

			HostView.Post(() =>
			{
				var imm = (global::Android.Views.InputMethods.InputMethodManager)
					HostView.Context.GetSystemService(global::Android.Content.Context.InputMethodService);
				HostView.RequestFocus();
				imm.ShowSoftInput(HostView, global::Android.Views.InputMethods.ShowFlags.Forced);
			});
		}

		public void StopTextInput()
		{
			if (HostView == null)
				return;

			HostView.Post(() =>
			{
				var imm = (global::Android.Views.InputMethods.InputMethodManager)
					HostView.Context.GetSystemService(global::Android.Content.Context.InputMethodService);
				imm.HideSoftInputFromWindow(HostView.WindowToken, 0);
			});
		}

		public void Dispose()
		{
			if (eglDisplay != Egl.EGL_NO_DISPLAY)
			{
				if (eglSurface != Egl.EGL_NO_SURFACE)
				{
					Egl.eglDestroySurface(eglDisplay, eglSurface);
					eglSurface = Egl.EGL_NO_SURFACE;
				}

				if (eglContext != Egl.EGL_NO_CONTEXT)
				{
					Egl.eglDestroyContext(eglDisplay, eglContext);
					eglContext = Egl.EGL_NO_CONTEXT;
				}

				Egl.eglTerminate(eglDisplay);
				eglDisplay = Egl.EGL_NO_DISPLAY;
			}
		}

		sealed class NullHardwareCursor : IHardwareCursor
		{
			public void Dispose() { }
		}
	}
}
