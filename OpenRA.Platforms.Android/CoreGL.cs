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
using System.Runtime.InteropServices;

namespace OpenRA.Platforms.Android
{
	// Fallback resolver for core GLES entry points. On conformant Android drivers
	// eglGetProcAddress returns valid pointers for core functions too, so this returns
	// IntPtr.Zero and lets the EGL path be authoritative. It exists as a seam so that if
	// a specific driver misbehaves we can add direct [DllImport] entries here without
	// touching the loader logic.
	static class CoreGL
	{
		public static IntPtr GetProcAddress(string name)
		{
			// Currently no fallbacks are needed on the target device. Add per-function
			// [DllImport("libGLESv2.so")] resolvers here if a driver proves problematic.
			return IntPtr.Zero;
		}
	}
}
