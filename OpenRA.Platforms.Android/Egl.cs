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
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace OpenRA.Platforms.Android
{
	// Minimal EGL 1.4 P/Invoke surface against libEGL.so. Android exposes EGL and GLES as
	// public NDK libraries, so direct P/Invoke is safe. We only need what the window/context
	// lifecycle requires; all GL entry points are loaded via OpenGLES.Bind in OpenGLES.cs.
	[SuppressMessage("Microsoft.StyleCop.CSharp.NamingRules", "SA1310:FieldNamesMustNotContainUnderscore",
		Justification = "C-style naming is kept for consistency with the underlying native API.")]
	[SuppressMessage("Style", "IDE1006:Naming Styles",
		Justification = "C-style naming is kept for consistency with the underlying native API.")]
	static class Egl
	{
		public const int EGL_DEFAULT_DISPLAY = 0;
		public static readonly IntPtr EGL_NO_DISPLAY = IntPtr.Zero;
		public static readonly IntPtr EGL_NO_CONTEXT = IntPtr.Zero;
		public static readonly IntPtr EGL_NO_SURFACE = IntPtr.Zero;

		public const int EGL_ALPHA_SIZE = 0x3031;
		public const int EGL_BLUE_SIZE = 0x3022;
		public const int EGL_GREEN_SIZE = 0x3023;
		public const int EGL_RED_SIZE = 0x3024;
		public const int EGL_DEPTH_SIZE = 0x3025;
		public const int EGL_STENCIL_SIZE = 0x3026;
		public const int EGL_SURFACE_TYPE = 0x3033;
		public const int EGL_WINDOW_BIT = 0x0004;
		public const int EGL_RENDERABLE_TYPE = 0x3040;
		public const int EGL_OPENGL_ES3_BIT = 0x0040;
		public const int EGL_OPENGL_ES2_BIT = 0x0004;
		public const int EGL_NONE = 0x3038;
		public const int EGL_CONTEXT_CLIENT_VERSION = 0x3098;
		public const int EGL_CONTEXT_MAJOR_VERSION = 0x3098;
		public const int EGL_VERSION = 0x3059;

		[DllImport("libEGL.so", EntryPoint = "eglGetDisplay")]
		public static extern IntPtr eglGetDisplay(IntPtr display_id);

		[DllImport("libEGL.so", EntryPoint = "eglInitialize")]
		public static extern bool eglInitialize(IntPtr dpy, out int major, out int minor);

		[DllImport("libEGL.so", EntryPoint = "eglChooseConfig")]
		public static extern bool eglChooseConfig(IntPtr dpy, int[] attrib_list, out IntPtr config, int config_size, out int num_config);

		[DllImport("libEGL.so", EntryPoint = "eglCreateContext")]
		public static extern IntPtr eglCreateContext(IntPtr dpy, IntPtr config, IntPtr share_context, int[] attrib_list);

		[DllImport("libEGL.so", EntryPoint = "eglDestroyContext")]
		public static extern bool eglDestroyContext(IntPtr dpy, IntPtr ctx);

		[DllImport("libEGL.so", EntryPoint = "eglCreateWindowSurface")]
		public static extern IntPtr eglCreateWindowSurface(IntPtr dpy, IntPtr config, IntPtr win, int[] attrib_list);

		[DllImport("libEGL.so", EntryPoint = "eglDestroySurface")]
		public static extern bool eglDestroySurface(IntPtr dpy, IntPtr surface);

		[DllImport("libEGL.so", EntryPoint = "eglTerminate")]
		public static extern bool eglTerminate(IntPtr dpy);

		[DllImport("libEGL.so", EntryPoint = "eglMakeCurrent")]
		public static extern bool eglMakeCurrent(IntPtr dpy, IntPtr draw, IntPtr read, IntPtr ctx);

		[DllImport("libEGL.so", EntryPoint = "eglSwapBuffers")]
		public static extern bool eglSwapBuffers(IntPtr dpy, IntPtr surface);

		[DllImport("libEGL.so", EntryPoint = "eglGetProcAddress")]
		public static extern IntPtr eglGetProcAddress(string name);

		[DllImport("libEGL.so", EntryPoint = "eglQueryString")]
		public static extern IntPtr eglQueryString(IntPtr dpy, int name);

		[DllImport("libEGL.so", EntryPoint = "eglGetError")]
		public static extern int eglGetError();

		public const int EGL_SUCCESS = 0x3000;

		public static string QueryString(IntPtr dpy, int name)
		{
			var ptr = eglQueryString(dpy, name);
			return ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr);
		}

		public static void Check(string context)
		{
			var err = eglGetError();
			if (err != EGL_SUCCESS)
				throw new InvalidOperationException($"EGL error 0x{err:X} during {context}");
		}

		// libandroid.so: obtain an ANativeWindow* from a Java android.view.Surface object.
		// eglCreateWindowSurface needs the raw ANativeWindow*, NOT the Java Surface handle.
		[DllImport("libandroid.so", EntryPoint = "ANativeWindow_fromSurface")]
		public static extern IntPtr ANativeWindow_fromSurface(IntPtr jnienv, IntPtr surface);
	}
}
