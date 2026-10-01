using System;

namespace ADOFAIEditorExtension.Features.PagerList
{
    /// <summary>
    /// Window rectangle in the parent canvas' local space.
    /// Convention: X grows right, Y grows up (the parent UI space, matching every other component),
    /// so Bottom is the smaller Y and Top is the larger Y. Width and Height are never negative.
    /// </summary>
    internal struct WindowRect
    {
        public float X;
        public float Y;
        public float Width;
        public float Height;

        public WindowRect(float x, float y, float width, float height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        /// <summary>Left edge (minimum X).</summary>
        public float Left { get { return X; } }

        /// <summary>Right edge (maximum X).</summary>
        public float Right { get { return X + Width; } }

        /// <summary>Bottom edge (minimum Y).</summary>
        public float Bottom { get { return Y; } }

        /// <summary>Top edge (maximum Y).</summary>
        public float Top { get { return Y + Height; } }

        /// <summary>Horizontal center.</summary>
        public float CenterX { get { return X + Width * 0.5f; } }

        /// <summary>Vertical center.</summary>
        public float CenterY { get { return Y + Height * 0.5f; } }
    }

    [System.Flags]
    internal enum WindowEdges
    {
        None = 0,
        Left = 1,
        Right = 2,
        Bottom = 4,
        Top = 8
    }

    /// <summary>
    /// Pure geometry for the pager window. No Unity or game types: every method is deterministic and
    /// receives the available parent bounds from the caller, so results are unit testable.
    /// </summary>
    internal static class PagerWindowGeometry
    {
        /// <summary>Smallest allowed window scale.</summary>
        public const float MinScale = 0.5f;

        /// <summary>Largest allowed window scale.</summary>
        public const float MaxScale = 2.5f;

        /// <summary>Scale used when the requested one is unusable.</summary>
        public const float DefaultScale = 1f;

        /// <summary>Distance in parent units at which an edge starts sticking to a bounds edge.</summary>
        public const float SnapEnterPixels = 16f;

        /// <summary>Distance in parent units at which a stuck edge releases again.</summary>
        public const float SnapReleasePixels = 28f;

        private const float Epsilon = 0.001f;

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static float Safe(float value, float fallback)
        {
            return Finite(value) ? value : fallback;
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        /// <summary>Normalizes a scale request into the supported range, falling back to 1 for NaN/Infinity.</summary>
        public static float SanitizeScale(float scale)
        {
            if (!Finite(scale)) return DefaultScale;
            if (scale < MinScale) return MinScale;
            if (scale > MaxScale) return MaxScale;
            return scale;
        }

        /// <summary>
        /// Normalizes a rectangle (negative sizes folded into the position), clamps its size to what
        /// fits inside <paramref name="bounds"/> and clamps its position so it stays inside. The
        /// effective minimum is always tightened to the space actually available, so even a
        /// degenerate or tiny bounds cannot produce a window that overflows it. Never returns NaN or
        /// Infinity.
        /// </summary>
        public static WindowRect Constrain(WindowRect rect, WindowRect bounds, float minWidth, float minHeight)
        {
            rect = Normalize(rect);
            bounds = Normalize(bounds);

            float boundsX = Safe(bounds.X, 0f);
            float boundsY = Safe(bounds.Y, 0f);
            float boundsWidth = (Finite(bounds.Width) && bounds.Width > 0f) ? bounds.Width : 0f;
            float boundsHeight = (Finite(bounds.Height) && bounds.Height > 0f) ? bounds.Height : 0f;
            float boundsRight = boundsX + boundsWidth;
            float boundsTop = boundsY + boundsHeight;

            float requestedMinWidth = Safe(minWidth, 0f);
            if (requestedMinWidth < 0f) requestedMinWidth = 0f;
            float requestedMinHeight = Safe(minHeight, 0f);
            if (requestedMinHeight < 0f) requestedMinHeight = 0f;

            // The minimum can never exceed the bounds: on a tiny canvas the window is capped by the
            // bounds instead of hanging out of them.
            float minW = requestedMinWidth < boundsWidth ? requestedMinWidth : boundsWidth;
            float minH = requestedMinHeight < boundsHeight ? requestedMinHeight : boundsHeight;

            float width = Safe(rect.Width, minW);
            float height = Safe(rect.Height, minH);
            width = Clamp(width, minW, boundsWidth);
            height = Clamp(height, minH, boundsHeight);

            // When the minimum was tightened, the window is anchored to the low bounds edge on that
            // axis; otherwise a normal rectangle is simply kept inside the bounds.
            float x = Safe(rect.X, boundsX);
            float maxX = minW > boundsWidth ? boundsX : boundsRight - width;
            x = Clamp(x, boundsX, maxX);

            float y = Safe(rect.Y, boundsY);
            float maxY = minH > boundsHeight ? boundsY : boundsTop - height;
            y = Clamp(y, boundsY, maxY);

            return new WindowRect(x, y, width, height);
        }

        /// <summary>
        /// Resizes <paramref name="start"/> by dragging the given <paramref name="edge"/> (a single
        /// edge or the two edges of a corner) by (dx, dy). The start rectangle is first constrained
        /// into <paramref name="bounds"/> at the minimum size, so an off-screen or oversized input
        /// still yields a valid result. Edges that are not dragged stay fixed: only the dragged edge
        /// moves, and only it is limited by the bounds and by minWidth/minHeight. With X right and Y
        /// up, Top is Y + Height and Bottom is Y, so a positive dy grows Y (moves the bottom edge up
        /// the screen) or height (moves the top edge further up).
        /// </summary>
        public static WindowRect Resize(WindowRect start, WindowEdges edge, float dx, float dy, WindowRect bounds, float minWidth, float minHeight)
        {
            start = Normalize(start);
            bounds = Normalize(bounds);

            dx = Safe(dx, 0f);
            dy = Safe(dy, 0f);

            float minW = Safe(minWidth, 0f);
            if (minW < 0f) minW = 0f;
            float minH = Safe(minHeight, 0f);
            if (minH < 0f) minH = 0f;

            float boundsWidth = (Finite(bounds.Width) && bounds.Width > 0f) ? bounds.Width : 0f;
            float boundsHeight = (Finite(bounds.Height) && bounds.Height > 0f) ? bounds.Height : 0f;

            // The minimum can never exceed the bounds: on a canvas smaller than the requested
            // minimum, the bounds win instead of the window growing out of them.
            if (minW > boundsWidth) minW = boundsWidth;
            if (minH > boundsHeight) minH = boundsHeight;

            float rectX = start.X;
            float rectY = start.Y;

            // The legal, minimum-sized start rectangle the drag is measured from.
            WindowRect legalStart = Constrain(start, bounds, minW, minH);
            float startX = legalStart.X;
            float startY = legalStart.Y;
            float startWidth = legalStart.Width;
            float startHeight = legalStart.Height;
            float startRight = startX + startWidth;
            float startTop = startY + startHeight;

            float boundsX = Safe(bounds.X, 0f);
            float boundsY = Safe(bounds.Y, 0f);
            float boundsRight = boundsX + boundsWidth;
            float boundsTop = boundsY + boundsHeight;

            bool movingLeft = (edge & WindowEdges.Left) == WindowEdges.Left;
            bool movingRight = (edge & WindowEdges.Right) == WindowEdges.Right;
            bool movingBottom = (edge & WindowEdges.Bottom) == WindowEdges.Bottom;
            bool movingTop = (edge & WindowEdges.Top) == WindowEdges.Top;

            // X: Left is the low edge, Right the high edge. The dragged edge moves within the bounds
            // and stops minWidth short of the opposite edge, which keeps its exact position. A start
            // narrower than the minimum was already grown to the minimum by Constrain.
            float left = startX;
            if (movingLeft)
            {
                left = Clamp(startX + dx, boundsX, startRight - minW);
            }

            float right = startRight;
            if (movingRight)
            {
                right = Clamp(startRight + dx, startX + minW, boundsRight);
            }

            // Y: Bottom is the low edge, Top the high edge; the bounds grow upwards too.
            float bottom = startY;
            if (movingBottom)
            {
                bottom = Clamp(startY + dy, boundsY, startTop - minH);
            }

            float top = startTop;
            if (movingTop)
            {
                top = Clamp(startTop + dy, startY + minH, boundsTop);
            }

            float resultWidth = right - left;
            if (resultWidth < 0f) resultWidth = 0f;
            float resultHeight = top - bottom;
            if (resultHeight < 0f) resultHeight = 0f;

            // When both edges of an axis are dragged together the size must not change, so the pair
            // is slid back inside the bounds instead of being clamped edge by edge.
            if (movingLeft && movingRight)
            {
                float width = startRight - startX;
                if (left + width > boundsRight) left = boundsRight - width;
                if (left < boundsX) left = boundsX;
                resultWidth = width;
            }

            if (movingBottom && movingTop)
            {
                float height = startTop - startY;
                if (bottom + height > boundsTop) bottom = boundsTop - height;
                if (bottom < boundsY) bottom = boundsY;
                resultHeight = height;
            }

            return new WindowRect(left, bottom, resultWidth, resultHeight);
        }

        /// <summary>
        /// Moves the window by a total delta measured from the un-snapped <paramref name="start"/>
        /// rectangle. The raw target is computed first and the snap state is decided from that raw
        /// target, so state is never accumulated from an already-snapped position.
        /// <paramref name="snappedEdges"/> carries the previous frame's snapping and is updated in
        /// place. Only distances between the window edges and the bounds edges are considered, and
        /// snapping never changes the window size.
        /// </summary>
        public static WindowRect Move(WindowRect start, float dx, float dy, WindowRect bounds, bool snap, float enterThreshold, float releaseThreshold, ref WindowEdges snappedEdges)
        {
            start = Normalize(start);
            bounds = Normalize(bounds);

            dx = Safe(dx, 0f);
            dy = Safe(dy, 0f);

            float enter = Safe(enterThreshold, 0f);
            if (enter < 0f) enter = 0f;
            float release = Safe(releaseThreshold, enter);
            if (release < 0f) release = 0f;
            if (release < enter) release = enter;

            float boundsX = Safe(bounds.X, 0f);
            float boundsY = Safe(bounds.Y, 0f);
            float boundsWidth = (Finite(bounds.Width) && bounds.Width > 0f) ? bounds.Width : 0f;
            float boundsHeight = (Finite(bounds.Height) && bounds.Height > 0f) ? bounds.Height : 0f;
            float boundsRight = boundsX + boundsWidth;
            float boundsTop = boundsY + boundsHeight;

            float width = Safe(start.Width, 0f);
            if (width < 0f) width = 0f;
            float height = Safe(start.Height, 0f);
            if (height < 0f) height = 0f;

            // Raw target from the untouched start rectangle, never from a previous snapped position.
            float rawX = Safe(start.X, boundsX) + dx;
            float rawY = Safe(start.Y, boundsY) + dy;
            float rawRight = rawX + width;
            float rawTop = rawY + height;

            WindowEdges sticky = snappedEdges;
            WindowEdges result = WindowEdges.None;
            float snappedX = float.NaN;
            float snappedY = float.NaN;

            bool stickyLeft = (sticky & WindowEdges.Left) == WindowEdges.Left;
            bool stickyRight = (sticky & WindowEdges.Right) == WindowEdges.Right;
            bool stickyBottom = (sticky & WindowEdges.Bottom) == WindowEdges.Bottom;
            bool stickyTop = (sticky & WindowEdges.Top) == WindowEdges.Top;

            if (snap && boundsWidth > 0f)
            {
                if (stickyLeft)
                {
                    float resolved = ResolveSnap(rawX, boundsX, true, 0f, release);
                    if (!float.IsNaN(resolved)) { snappedX = resolved; result |= WindowEdges.Left; }
                }
                else if (stickyRight)
                {
                    // The right edge is glued by positioning the window, not by clamping afterwards.
                    float resolved = ResolveSnap(rawRight, boundsRight, true, 0f, release);
                    if (!float.IsNaN(resolved)) { snappedX = resolved - width; result |= WindowEdges.Right; }
                }

                if (float.IsNaN(snappedX))
                {
                    float toLeft = ResolveSnap(rawX, boundsX, false, enter, enter);
                    float toRight = ResolveSnap(rawRight, boundsRight, false, enter, enter);
                    bool hasLeft = !float.IsNaN(toLeft);
                    bool hasRight = !float.IsNaN(toRight);

                    if (hasLeft && hasRight)
                    {
                        // Both bounds edges are within reach: take the nearest one.
                        float distanceToLeft = Math.Abs(rawX - boundsX);
                        float distanceToRight = Math.Abs(rawRight - boundsRight);
                        if (distanceToLeft <= distanceToRight) { snappedX = toLeft; result |= WindowEdges.Left; }
                        else { snappedX = toRight - width; result |= WindowEdges.Right; }
                    }
                    else if (hasLeft) { snappedX = toLeft; result |= WindowEdges.Left; }
                    else if (hasRight) { snappedX = toRight - width; result |= WindowEdges.Right; }
                }
            }

            if (snap && boundsHeight > 0f)
            {
                if (stickyBottom)
                {
                    float resolved = ResolveSnap(rawY, boundsY, true, 0f, release);
                    if (!float.IsNaN(resolved)) { snappedY = resolved; result |= WindowEdges.Bottom; }
                }
                else if (stickyTop)
                {
                    // The top edge is glued by positioning the window below bounds.Top.
                    float resolved = ResolveSnap(rawTop, boundsTop, true, 0f, release);
                    if (!float.IsNaN(resolved)) { snappedY = resolved - height; result |= WindowEdges.Top; }
                }

                if (float.IsNaN(snappedY))
                {
                    float toBottom = ResolveSnap(rawY, boundsY, false, enter, enter);
                    float toTop = ResolveSnap(rawTop, boundsTop, false, enter, enter);
                    bool hasBottom = !float.IsNaN(toBottom);
                    bool hasTop = !float.IsNaN(toTop);

                    if (hasTop && hasBottom)
                    {
                        float distanceToBottom = Math.Abs(rawY - boundsY);
                        float distanceToTop = Math.Abs(rawTop - boundsTop);
                        if (distanceToBottom <= distanceToTop) { snappedY = toBottom; result |= WindowEdges.Bottom; }
                        else { snappedY = toTop - height; result |= WindowEdges.Top; }
                    }
                    else if (hasBottom) { snappedY = toBottom; result |= WindowEdges.Bottom; }
                    else if (hasTop) { snappedY = toTop - height; result |= WindowEdges.Top; }
                }
            }

            float finalX = !float.IsNaN(snappedX) ? snappedX : rawX;
            float finalY = !float.IsNaN(snappedY) ? snappedY : rawY;

            // A raw target dragged off screen still lands inside the bounds.
            float maxX = boundsRight - width;
            if (maxX < boundsX) maxX = boundsX;
            float maxY = boundsTop - height;
            if (maxY < boundsY) maxY = boundsY;
            finalX = Clamp(finalX, boundsX, maxX);
            finalY = Clamp(finalY, boundsY, maxY);

            snappedEdges = result;
            return new WindowRect(finalX, finalY, width, height);
        }

        /// <summary>
        /// Returns the position that puts a window edge exactly on a bounds edge, or NaN when that
        /// edge is out of reach. <paramref name="sticky"/> keeps an attached edge glued while it
        /// stays inside the release distance; a fresh attachment needs the enter distance.
        /// </summary>
        private static float ResolveSnap(float edge, float target, bool sticky, float enter, float release)
        {
            if (!Finite(edge) || !Finite(target)) return float.NaN;
            float limit = sticky ? release : enter;
            return Math.Abs(edge - target) <= limit ? target : float.NaN;
        }

        /// <summary>Folds a negative width/height into the position so Bottom/Left is the minimum.</summary>
        private static WindowRect Normalize(WindowRect rect)
        {
            float x = Finite(rect.X) ? rect.X : 0f;
            float y = Finite(rect.Y) ? rect.Y : 0f;
            float width = Finite(rect.Width) ? rect.Width : 0f;
            float height = Finite(rect.Height) ? rect.Height : 0f;

            if (width < 0f)
            {
                x += width;
                width = -width;
            }

            if (height < 0f)
            {
                y += height;
                height = -height;
            }

            if (width < Epsilon) width = 0f;
            if (height < Epsilon) height = 0f;

            return new WindowRect(x, y, width, height);
        }
    }
}
