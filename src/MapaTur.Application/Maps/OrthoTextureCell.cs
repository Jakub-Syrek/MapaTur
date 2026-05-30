namespace MapaTur.Application.Maps;

/// <summary>
/// A single ortho texture cell produced by <see cref="MBTilesOrthoCompositor"/>. One
/// cell per <c>(Row, Col)</c> of the requested mesh ortho grid; the renderer uploads
/// these directly as GL textures keyed by <c>OrthoTileIndex = Row * gridCols + Col</c>.
/// </summary>
/// <param name="Row">Row in the ortho grid, 0 = north.</param>
/// <param name="Col">Column in the ortho grid, 0 = west.</param>
/// <param name="Width">Pixel width of the cell texture.</param>
/// <param name="Height">Pixel height of the cell texture.</param>
/// <param name="Rgba">Top-row-first RGBA8 pixel buffer (length = <c>Width * Height * 4</c>).</param>
public sealed record OrthoTextureCell(int Row, int Col, int Width, int Height, byte[] Rgba);