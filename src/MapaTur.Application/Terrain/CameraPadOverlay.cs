namespace MapaTur.Application.Terrain;

/// <summary>
/// Continuous camera action a pad button drives while held. Mirrors the XAML pads 1:1
/// (AltitudePad: Raise/Lower; PanTiltPad: pan/rotate/tilt).
/// </summary>
public enum CameraPadAction
{
    Raise,
    Lower,
    PanForward,
    PanBack,
    PanLeft,
    PanRight,
    RotateLeftSlow,
    RotateRightSlow,
    LookUp,
    LookDown,
}

/// <summary>
/// One round pad button, laid out in surface PIXELS: <paramref name="X"/>/<paramref name="Y"/> is the
/// top-left of the button square, <paramref name="Size"/> its edge (buttons are circles of radius
/// Size/2 — the XAML style used CornerRadius = size/2). <paramref name="Glyph"/> is the same unicode
/// arrow the XAML button showed.
/// </summary>
public readonly record struct CameraPadButton(CameraPadAction Action, float X, float Y, float Size, string Glyph);

/// <summary>
/// Geometry of the Skia-drawn camera pads (task #8): the XAML AltitudePad/PanTiltPad overlaid on the
/// SwapChainPanel are what pumps WinUI composition surfaces into dedicated VRAM (~90% of the gpuDed
/// creep, ETW VidMm 2026-08-26), so on Windows the pads are drawn INSIDE the GL/Skia surface and this
/// class is their single source of layout truth. Pure and unit-tested; the view only rasterises the
/// rects/glyphs it gets here and forwards pointer hits.
///
/// Layout law (mirrors the removed XAML, in DIPs, scaled by <c>scale</c> to pixels):
/// margin 12 from the viewport edges, spacing 4, large button 46, small (tilt) 34 centred in its cell.
/// Bottom-left: column [Raise ▲ / Lower ▼]. Bottom-right 3×4 grid:
/// row0 [⟲ ▲ ⟳], row1 [◀ ⤒ ▶], row2 [ ⤓ ], row3 [ ▼] — same shape the user accepted in XAML.
/// </summary>
public static class CameraPadOverlay
{
    /// <summary>Large (standard) button edge in DIPs.</summary>
    public const float ButtonDip = 46f;

    /// <summary>Small (tilt) button edge in DIPs.</summary>
    public const float SmallButtonDip = 34f;

    /// <summary>Distance of both pads from the viewport edges, DIPs.</summary>
    public const float MarginDip = 12f;

    /// <summary>Gap between neighbouring buttons, DIPs.</summary>
    public const float SpacingDip = 4f;

    /// <summary>Idle button opacity — the XAML style's translucent resting state.</summary>
    public const float IdleOpacity = 0.35f;

    /// <summary>Opacity while a button is held — the XAML "materialise on press" state.</summary>
    public const float PressedOpacity = 1.0f;

    /// <summary>
    /// Lays out all ten buttons for a surface of <paramref name="widthPx"/>×<paramref name="heightPx"/>
    /// pixels at <paramref name="scale"/> pixels-per-DIP.
    /// </summary>
    public static IReadOnlyList<CameraPadButton> Layout(float widthPx, float heightPx, float scale)
    {
        float btn = ButtonDip * scale;
        float small = SmallButtonDip * scale;
        float margin = MarginDip * scale;
        float gap = SpacingDip * scale;

        var buttons = new List<CameraPadButton>(10);

        // AltitudePad: kolumna dwoch duzych przyciskow, dolny styka sie z dolnym marginesem.
        float lowerY = heightPx - margin - btn;
        buttons.Add(new CameraPadButton(CameraPadAction.Raise, margin, lowerY - gap - btn, btn, "▲"));
        buttons.Add(new CameraPadButton(CameraPadAction.Lower, margin, lowerY, btn, "▼"));

        // PanTiltPad: siatka 3 kolumn (wszystkie szerokosci duzego przycisku) x 4 wierszy
        // (duzy, duzy, maly, duzy) — wysokosc wiersza = najwiekszy przycisk w nim (jak Auto w XAML).
        float gridW = (3f * btn) + (2f * gap);
        float gridLeft = widthPx - margin - gridW;
        float[] rowHeights = [btn, btn, small, btn];
        float gridH = rowHeights.Sum() + (3f * gap);
        float gridTop = heightPx - margin - gridH;

        float ColX(int col) => gridLeft + (col * (btn + gap));
        float RowY(int row)
        {
            float y = gridTop;
            for (int r = 0; r < row; r++)
            {
                y += rowHeights[r] + gap;
            }

            return y;
        }

        // Maly przycisk siedzi wysrodkowany w celi swojej kolumny/wiersza (XAML: HeightRequest 34 w Auto-celi).
        float CenterInCol(int col, float size) => ColX(col) + ((btn - size) / 2f);
        float CenterInRow(int row, float size) => RowY(row) + ((rowHeights[row] - size) / 2f);

        buttons.Add(new CameraPadButton(CameraPadAction.RotateLeftSlow, ColX(0), RowY(0), btn, "↺"));
        buttons.Add(new CameraPadButton(CameraPadAction.PanForward, ColX(1), RowY(0), btn, "▲"));
        buttons.Add(new CameraPadButton(CameraPadAction.RotateRightSlow, ColX(2), RowY(0), btn, "↻"));
        buttons.Add(new CameraPadButton(CameraPadAction.PanLeft, ColX(0), RowY(1), btn, "◀"));
        buttons.Add(new CameraPadButton(CameraPadAction.LookUp, CenterInCol(1, small), CenterInRow(1, small), small, "⤒"));
        buttons.Add(new CameraPadButton(CameraPadAction.PanRight, ColX(2), RowY(1), btn, "▶"));
        buttons.Add(new CameraPadButton(CameraPadAction.LookDown, CenterInCol(1, small), CenterInRow(2, small), small, "⤓"));
        buttons.Add(new CameraPadButton(CameraPadAction.PanBack, ColX(1), RowY(3), btn, "▼"));

        return buttons;
    }

    /// <summary>
    /// The button under pixel (<paramref name="xPx"/>, <paramref name="yPx"/>), or null. Buttons are
    /// circles — a press in the square's corner falls through to the camera-drag gestures beneath.
    /// </summary>
    public static CameraPadAction? HitTest(IReadOnlyList<CameraPadButton> layout, float xPx, float yPx)
    {
        foreach (CameraPadButton b in layout)
        {
            float r = b.Size / 2f;
            float dx = xPx - (b.X + r);
            float dy = yPx - (b.Y + r);
            if ((dx * dx) + (dy * dy) <= r * r)
            {
                return b.Action;
            }
        }

        return null;
    }
}