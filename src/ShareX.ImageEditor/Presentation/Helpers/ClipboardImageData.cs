#region License Information (GPL v3)

/*
    ShareX.ImageEditor - The UI-agnostic Editor library for ShareX
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using Avalonia.Input;
using Avalonia.Media.Imaging;

namespace ShareX.ImageEditor.Presentation.Helpers;

/// <summary>Clipboard data for an image.</summary>
public static class ClipboardImageData
{
    /// <summary>
    /// Creates clipboard data that decodes <paramref name="pngBytes"/> into a new bitmap for each request.
    /// While this application owns the clipboard, Avalonia hands readers the stored value itself, and readers
    /// dispose the bitmap they get; a shared bitmap would break the next read and the next paste in another application.
    /// </summary>
    public static DataTransfer Create(byte[] pngBytes)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);

        var data = new DataTransfer();
        var item = new DataTransferItem();
        item.Set(DataFormat.Bitmap, () =>
        {
            using var stream = new MemoryStream(pngBytes, writable: false);
            return new Bitmap(stream);
        });
        data.Add(item);
        return data;
    }
}
