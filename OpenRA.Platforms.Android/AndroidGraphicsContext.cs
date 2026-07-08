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
using OpenRA;
using OpenRA.Primitives;

namespace OpenRA.Platforms.Android
{
	// Direct (single-threaded) GLES context backed by EGL. The EGL context is made current on
	// the thread that calls InitializeOpenGL, which must be the same thread that drives Game.Loop.
	// This mirrors the Windows-windowed single-threaded path in Sdl2PlatformWindow and avoids
	// ThreadedGraphicsContext for the Phase 0 menu-rendering milestone.
	sealed class AndroidGraphicsContext : ThreadAffine, IGraphicsContext
	{
		readonly AndroidPlatformWindow window;
		uint vao;

		public string GLVersion => OpenGL.Version;

		public AndroidGraphicsContext(AndroidPlatformWindow window)
		{
			this.window = window;
		}

		// Called by the window once the EGL context + surface are created, on the game thread.
		internal void InitializeOpenGL()
		{
			SetThreadAffinity();

			if (!Egl.eglMakeCurrent(window.Display, window.Surface, window.Surface, window.ContextPtr))
				throw new InvalidOperationException("Failed to make EGL context current.");

			OpenGL.Initialize();
			OpenGL.CheckGLError();

			OpenGL.glGenVertexArrays(1, out vao);
			OpenGL.CheckGLError();
			OpenGL.glBindVertexArray(vao);
			OpenGL.CheckGLError();
		}

		public IVertexBuffer<T> CreateEmptyVertexBuffer<T>(int size) where T : struct
		{
			VerifyThreadAffinity();
			return new VertexBuffer<T>(size);
		}

		public IVertexBuffer<T> CreateVertexBuffer<T>(T[] data, bool dynamic = true) where T : struct
		{
			VerifyThreadAffinity();
			return new VertexBuffer<T>(data, dynamic);
		}

		public IIndexBuffer CreateIndexBuffer(uint[] indices)
		{
			VerifyThreadAffinity();
			return new StaticIndexBuffer(indices);
		}

		public T[] CreateVertices<T>(int size) where T : struct
		{
			VerifyThreadAffinity();
			return new T[size];
		}

		public ITexture CreateTexture()
		{
			VerifyThreadAffinity();
			return new Texture();
		}

		public IFrameBuffer CreateFrameBuffer(Size s)
		{
			VerifyThreadAffinity();
			return new FrameBuffer(s, new Texture(), Color.FromArgb(0));
		}

		public IFrameBuffer CreateFrameBuffer(Size s, Color clearColor)
		{
			VerifyThreadAffinity();
			return new FrameBuffer(s, new Texture(), clearColor);
		}

		public IShader CreateShader(IShaderBindings bindings)
		{
			VerifyThreadAffinity();
			return new Shader(bindings);
		}

		public void EnableScissor(int x, int y, int width, int height)
		{
			VerifyThreadAffinity();

			if (width < 0)
				width = 0;

			if (height < 0)
				height = 0;

			var windowSize = window.EffectiveWindowSize;
			var windowScale = window.EffectiveWindowScale;
			var surfaceSize = window.SurfaceSize;

			if (windowSize != surfaceSize)
			{
				x = (int)Math.Round(windowScale * x);
				y = (int)Math.Round(windowScale * y);
				width = (int)Math.Round(windowScale * width);
				height = (int)Math.Round(windowScale * height);
			}

			OpenGL.glScissor(x, y, width, height);
			OpenGL.CheckGLError();
			OpenGL.glEnable(OpenGL.GL_SCISSOR_TEST);
			OpenGL.CheckGLError();
		}

		public void DisableScissor()
		{
			VerifyThreadAffinity();
			OpenGL.glDisable(OpenGL.GL_SCISSOR_TEST);
			OpenGL.CheckGLError();
		}

		public void Present()
		{
			VerifyThreadAffinity();

			// Reconcile the EGL surface with the Android surface lifecycle (regeneration on
			// pause/resume) before attempting to swap. All EGL surface work happens here, on the
			// game thread that owns the context.
			window.EnsureCurrentSurface();

			// If there's no valid surface to swap to (surface destroyed), skip silently rather than
			// spamming EGL_BAD_SURFACE every frame. The engine's suspended state also gates this.
			if (!window.SurfaceValid)
				return;

			if (!Egl.eglSwapBuffers(window.Display, window.Surface))
			{
				var err = Egl.eglGetError();
				if (err == 0x300D /* EGL_BAD_SURFACE */ || err == 0x300E /* EGL_BAD_NATIVE_WINDOW */)
				{
					// The native window died under us; force a recreation on the next frame.
					window.InvalidateSurface();
				}
				else
					Console.WriteLine($"eglSwapBuffers failed with EGL error 0x{err:X}");
			}
		}

		static int ModeFromPrimitiveType(PrimitiveType pt)
		{
			switch (pt)
			{
				case PrimitiveType.PointList: return OpenGL.GL_POINTS;
				case PrimitiveType.LineList: return OpenGL.GL_LINES;
				case PrimitiveType.TriangleList: return OpenGL.GL_TRIANGLES;
			}

			throw new NotImplementedException();
		}

		public void DrawPrimitives(PrimitiveType pt, int firstVertex, int numVertices)
		{
			VerifyThreadAffinity();
			OpenGL.glDrawArrays(ModeFromPrimitiveType(pt), firstVertex, numVertices);
			OpenGL.CheckGLError();
		}

		public void DrawElements(int numIndices, int offset)
		{
			VerifyThreadAffinity();
			OpenGL.glDrawElements(OpenGL.GL_TRIANGLES, numIndices, OpenGL.GL_UNSIGNED_INT, new IntPtr(offset));
			OpenGL.CheckGLError();
		}

		public void Clear()
		{
			VerifyThreadAffinity();
			OpenGL.glClearColor(0, 0, 0, 1);
			OpenGL.CheckGLError();
			OpenGL.glClear(OpenGL.GL_COLOR_BUFFER_BIT | OpenGL.GL_DEPTH_BUFFER_BIT);
			OpenGL.CheckGLError();
		}

		public void EnableDepthBuffer()
		{
			VerifyThreadAffinity();
			OpenGL.glClear(OpenGL.GL_DEPTH_BUFFER_BIT);
			OpenGL.CheckGLError();
			OpenGL.glEnable(OpenGL.GL_DEPTH_TEST);
			OpenGL.CheckGLError();
			OpenGL.glDepthFunc(OpenGL.GL_LEQUAL);
			OpenGL.CheckGLError();
		}

		public void DisableDepthBuffer()
		{
			VerifyThreadAffinity();
			OpenGL.glDisable(OpenGL.GL_DEPTH_TEST);
			OpenGL.CheckGLError();
		}

		public void ClearDepthBuffer()
		{
			VerifyThreadAffinity();
			OpenGL.glClear(OpenGL.GL_DEPTH_BUFFER_BIT);
			OpenGL.CheckGLError();
		}

		public void SetBlendMode(BlendMode mode)
		{
			VerifyThreadAffinity();
			OpenGL.glBlendEquation(OpenGL.GL_FUNC_ADD);
			OpenGL.CheckGLError();

			switch (mode)
			{
				case BlendMode.None:
					OpenGL.glDisable(OpenGL.GL_BLEND);
					break;
				case BlendMode.Alpha:
					OpenGL.glEnable(OpenGL.GL_BLEND);
					OpenGL.CheckGLError();
					OpenGL.glBlendFunc(OpenGL.GL_ONE, OpenGL.GL_ONE_MINUS_SRC_ALPHA);
					break;
				case BlendMode.Additive:
				case BlendMode.Subtractive:
					OpenGL.glEnable(OpenGL.GL_BLEND);
					OpenGL.CheckGLError();
					OpenGL.glBlendFunc(OpenGL.GL_ONE, OpenGL.GL_ONE);
					if (mode == BlendMode.Subtractive)
					{
						OpenGL.CheckGLError();
						OpenGL.glBlendEquationSeparate(OpenGL.GL_FUNC_REVERSE_SUBTRACT, OpenGL.GL_FUNC_ADD);
					}

					break;
				case BlendMode.Multiply:
					OpenGL.glEnable(OpenGL.GL_BLEND);
					OpenGL.CheckGLError();
					OpenGL.glBlendFunc(OpenGL.GL_DST_COLOR, OpenGL.GL_ONE_MINUS_SRC_ALPHA);
					OpenGL.CheckGLError();
					break;
				case BlendMode.Multiplicative:
					OpenGL.glEnable(OpenGL.GL_BLEND);
					OpenGL.CheckGLError();
					OpenGL.glBlendFunc(OpenGL.GL_ZERO, OpenGL.GL_SRC_COLOR);
					OpenGL.CheckGLError();
					break;
				case BlendMode.DoubleMultiplicative:
					OpenGL.glEnable(OpenGL.GL_BLEND);
					OpenGL.CheckGLError();
					OpenGL.glBlendFunc(OpenGL.GL_DST_COLOR, OpenGL.GL_SRC_COLOR);
					OpenGL.CheckGLError();
					break;
				case BlendMode.LowAdditive:
					OpenGL.glEnable(OpenGL.GL_BLEND);
					OpenGL.CheckGLError();
					OpenGL.glBlendFunc(OpenGL.GL_DST_COLOR, OpenGL.GL_ONE);
					OpenGL.CheckGLError();
					break;
				case BlendMode.Screen:
					OpenGL.glEnable(OpenGL.GL_BLEND);
					OpenGL.CheckGLError();
					OpenGL.glBlendFunc(OpenGL.GL_SRC_COLOR, OpenGL.GL_ONE_MINUS_SRC_COLOR);
					OpenGL.CheckGLError();
					break;
				case BlendMode.Translucent:
					OpenGL.glEnable(OpenGL.GL_BLEND);
					OpenGL.CheckGLError();
					OpenGL.glBlendFunc(OpenGL.GL_DST_COLOR, OpenGL.GL_ONE_MINUS_DST_COLOR);
					OpenGL.CheckGLError();
					break;
			}

			OpenGL.CheckGLError();
		}

		public void SetVSyncEnabled(bool enabled)
		{
			// Android is always vsynced at the compositor; eglSwapInterval is advisory and
			// typically ignored, so this is a no-op. Kept for interface parity.
		}

		public void Dispose()
		{
			// EGL context lifetime is owned by AndroidPlatformWindow, which tears it down
			// together with the surface in its Dispose.
		}
	}
}
