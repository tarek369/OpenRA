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
using System.Collections.Concurrent;
using System.Diagnostics;
using Android.Views;
using OpenRA;
using OpenRA.Primitives;

namespace OpenRA.Platforms.Android
{
	// Translates Android MotionEvents (multi-touch) into OpenRA MouseInputs.
	//
	// Touch model:
	//   - Single tap          = left click (with double-tap detection via MultiTapDetection)
	//   - Long press (>500ms) = right-click (context menu, unit orders)
	//   - Drag                = mouse move with left button held (scroll the map, drag-select)
	//   - Two-finger pinch    = scroll/zoom (synthesized as MouseInputEvent.Scroll)
	//   - Two-finger drag     = map pan (mouse move with right button held)
	sealed class AndroidInput
	{
		readonly ConcurrentQueue<PendingInput> pending = new();

		// Primary finger state (left button).
		int primaryPointerId = -1;
		int2 primaryDownPos;
		Stopwatch primaryDownTimer;

		// Secondary finger state (right button / pan).
		int secondaryPointerId = -1;

		// Pinch state.
		float lastPinchDist;

		// Long-press detection threshold.
		const int LongPressMs = 500;
		const int TouchSlopPx = 16;

		// Suppresses the Up event when a long-press already fired a right-click.
		bool longPressFired;

		struct PendingInput
		{
			public MotionEventActions Action;
			public float X;
			public float Y;
			public int PointerId;
			public long TimestampMs;
		}

		public void Enqueue(MotionEvent e, Size windowSize)
		{
			var action = e.ActionMasked;
			var index = e.ActionIndex;

			if (action == MotionEventActions.Move)
			{
				// Forward moves for all tracked fingers.
				for (var i = 0; i < e.PointerCount; i++)
				{
					var pid = e.GetPointerId(i);
					if (pid == primaryPointerId || pid == secondaryPointerId)
					{
						pending.Enqueue(new PendingInput
						{
							Action = action,
							X = e.GetX(i),
							Y = e.GetY(i),
							PointerId = pid,
							TimestampMs = e.EventTime
						});
					}
				}

				// Detect pinch zoom when two fingers are down.
				if (primaryPointerId >= 0 && secondaryPointerId >= 0 && e.PointerCount >= 2)
				{
					var i0 = e.FindPointerIndex(primaryPointerId);
					var i1 = e.FindPointerIndex(secondaryPointerId);
					if (i0 >= 0 && i1 >= 0)
					{
						var dx = e.GetX(i0) - e.GetX(i1);
						var dy = e.GetY(i0) - e.GetY(i1);
						var dist = (float)Math.Sqrt(dx * dx + dy * dy);
						if (lastPinchDist > 0)
						{
							var delta = (int)(dist - lastPinchDist);
							if (Math.Abs(delta) > 2)
							{
								pending.Enqueue(new PendingInput
								{
									Action = MotionEventActions.Scroll,
									X = (e.GetX(i0) + e.GetX(i1)) / 2,
									Y = (e.GetY(i0) + e.GetY(i1)) / 2,
									PointerId = -1,
									TimestampMs = e.EventTime
								});
								// Store the delta in the Y field via a side channel — we'll read it in PumpInput.
								pinchDelta = delta;
							}
						}

						lastPinchDist = dist;
					}
				}
			}
			else
			{
				pending.Enqueue(new PendingInput
				{
					Action = action,
					X = e.GetX(index),
					Y = e.GetY(index),
					PointerId = e.GetPointerId(index),
					TimestampMs = e.EventTime
				});
			}
		}

		int pinchDelta;

		public void PumpInput(IInputHandler inputHandler, Size windowSize, Size surfaceSize, float scale)
		{
			while (pending.TryDequeue(out var p))
			{
				var pos = new int2((int)p.X, (int)p.Y);

				switch (p.Action)
				{
					case MotionEventActions.Down:
						primaryPointerId = p.PointerId;
						primaryDownPos = pos;
						primaryDownTimer = Stopwatch.StartNew();
						longPressFired = false;
						var tapCount = MultiTapDetection.DetectFromMouse(0, pos);
						inputHandler.OnMouseInput(new MouseInput(MouseInputEvent.Down, MouseButton.Left, pos, int2.Zero, Modifiers.None, tapCount));
						break;

					case MotionEventActions.PointerDown:
						if (secondaryPointerId < 0)
						{
							secondaryPointerId = p.PointerId;
							lastPinchDist = 0;

							// If the primary finger is down, start a right-button drag (map pan).
							if (primaryPointerId >= 0 && !longPressFired)
							{
								inputHandler.OnMouseInput(new MouseInput(MouseInputEvent.Down, MouseButton.Right, pos, int2.Zero, Modifiers.None, 1));
							}
						}

						break;

					case MotionEventActions.Move:
						if (p.PointerId == primaryPointerId)
						{
							// Check for long-press (right-click) if the finger hasn't moved much.
							if (!longPressFired && primaryDownTimer != null && primaryDownTimer.ElapsedMilliseconds > LongPressMs)
							{
								var moved = (pos - primaryDownPos).Length;
								if (moved < TouchSlopPx)
								{
									longPressFired = true;
									inputHandler.OnMouseInput(new MouseInput(MouseInputEvent.Up, MouseButton.Left, pos, int2.Zero, Modifiers.None, 1));
									inputHandler.OnMouseInput(new MouseInput(MouseInputEvent.Down, MouseButton.Right, pos, int2.Zero, Modifiers.None, 1));
								}
							}
							else
							{
								inputHandler.OnMouseInput(new MouseInput(MouseInputEvent.Move, MouseButton.Left, pos, int2.Zero, Modifiers.None, 0));
							}
						}
						else if (p.PointerId == secondaryPointerId && primaryPointerId >= 0)
						{
							// Two-finger pan: move with right button.
							inputHandler.OnMouseInput(new MouseInput(MouseInputEvent.Move, MouseButton.Right, pos, int2.Zero, Modifiers.None, 0));
						}

						break;

					case MotionEventActions.PointerUp:
						if (p.PointerId == secondaryPointerId)
						{
							// End right-button drag.
							if (primaryPointerId >= 0 && !longPressFired)
								inputHandler.OnMouseInput(new MouseInput(MouseInputEvent.Up, MouseButton.Right, pos, int2.Zero, Modifiers.None, 1));
							secondaryPointerId = -1;
							lastPinchDist = 0;
						}

						break;

					case MotionEventActions.Up:
						if (longPressFired)
						{
							// The long-press already sent a right-click Down; send the matching Up.
							inputHandler.OnMouseInput(new MouseInput(MouseInputEvent.Up, MouseButton.Right, pos, int2.Zero, Modifiers.None, 1));
						}
						else
						{
							inputHandler.OnMouseInput(new MouseInput(MouseInputEvent.Up, MouseButton.Left, pos, int2.Zero, Modifiers.None, MultiTapDetection.InfoFromMouse(0)));
						}

						primaryPointerId = -1;
						primaryDownTimer = null;
						longPressFired = false;
						break;

					case MotionEventActions.Scroll:
						// Pinch-to-zoom: synthesize a scroll event. The zoom modifier (Ctrl) is needed
						// by ViewportControllerWidget, so we set it to make zoom work without a keyboard.
						inputHandler.OnMouseInput(new MouseInput(MouseInputEvent.Scroll, MouseButton.None, pos, new int2(0, pinchDelta), Modifiers.Ctrl, 0));
						pinchDelta = 0;
						break;
				}
			}
		}
	}
}
