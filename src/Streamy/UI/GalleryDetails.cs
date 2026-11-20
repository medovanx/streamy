using System.Globalization;
using Streamy.Core.Files;
using Streamy.Core.Media;
using Streamy.Services;

namespace Streamy.UI;

/// <summary>
/// The gallery's right-hand panel: a preview and the metadata of the selected photo or video (dates, size,
/// resolution, camera settings, orientation, favourite, the app that made it, where it is), or a summary
/// when several are selected.
/// </summary>
internal sealed class GalleryDetails : Panel
{
    private readonly string _serial;
    private readonly ThumbnailLoader _thumbs;
    private readonly LocationIndex _locations;
    private readonly PictureBox _preview = new() { Dock = DockStyle.Top, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(0x16, 0x16, 0x16) };
    private readonly Label _title = new() { Dock = DockStyle.Top, Height = 44, Font = new Font("Segoe UI Semibold", 10.5f), Padding = new Padding(12, 10, 12, 0), AutoEllipsis = true };
    private readonly TableLayoutPanel _table = new() { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12, 0, 12, 12), AutoScroll = true };
    private readonly Dictionary<long, MediaDetails> _details = [];
    private IReadOnlyList<MediaItem> _selection = [];

    public GalleryDetails(string serial, ThumbnailLoader thumbs, LocationIndex locations)
    {
        _serial = serial;
        _thumbs = thumbs;
        _locations = locations;
        Dock = DockStyle.Right;
        Width = (int)Math.Round(300 * DeviceDpi / 96.0);
        _preview.Height = (int)Math.Round(220 * DeviceDpi / 96.0);
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, (int)Math.Round(84 * DeviceDpi / 96.0)));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(_table);
        Controls.Add(_title);
        Controls.Add(_preview);
        ShowSelection([]);
    }

    public void ShowSelection(IReadOnlyList<MediaItem> items)
    {
        _selection = items;
        _table.SuspendLayout();
        _table.Controls.Clear();
        _table.RowStyles.Clear();
        SetPreview(items is [var one] ? one : null);

        if (items.Count == 0)
        {
            _title.Text = "Nothing selected";
            Row("", "Click a photo or video to see its details. Drag across thumbnails to select several.");
        }
        else if (items is [var item])
        {
            _title.Text = item.Name;
            Row("Taken", item.Taken.ToLocalTime().ToString("dddd d MMMM yyyy, HH:mm", CultureInfo.CurrentCulture));
            Row("Album", item.Album);
            Row("Type", item.Mime.Length > 0 ? item.Mime : Path.GetExtension(item.Name));
            Row("Size", PhoneFiles.FormatSize(item.Size));
            if (item.Width > 0)
                Row("Resolution", $"{item.Width} × {item.Height}" + (item.IsVideo ? "" : $" ({item.Width * (double)item.Height / 1e6:0.#} MP)"));
            if (item.IsVideo) Row("Length", item.DurationText);
            if (_details.TryGetValue(item.Id, out var d))
            {
                if (d.CameraSettings is { } cam) Row("Camera", cam);
                if (d.BitRate is { } br) Row("Bit rate", br);
                if (d.FrameRate is { } fr) Row("Frame rate", fr);
                if (d.Orientation is { } o) Row("Rotation", $"{o}°");
                if (d.CreatedBy is { } by) Row("Made by", by);
                if (d.Added is { } added) Row("Added", added.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture));
                if (d.IsFavorite) Row("Favourite", "Yes");
            }
            else
            {
                _ = LoadDetailsAsync(item);
            }
            if (_locations.Point(item) is { } point)
            {
                Row("Location", (_locations.Place(item) is { } place ? place + "\n" : "") + point);
                MapLink(point);
            }
            else if (!_locations.Known(item) && Core.Media.GeoTags.MayHaveLocation(item))
            {
                Row("Location", "Not read yet");
            }
            Row("Path", item.Path);
        }
        else
        {
            int videos = items.Count(i => i.IsVideo);
            _title.Text = $"{items.Count:N0} selected";
            Row("Photos", (items.Count - videos).ToString("N0"));
            Row("Videos", videos.ToString("N0"));
            Row("Total size", PhoneFiles.FormatSize(items.Sum(i => i.Size)));
            var first = items.Min(i => i.Taken).ToLocalTime();
            var last = items.Max(i => i.Taken).ToLocalTime();
            Row("Taken", first.Date == last.Date ? first.ToString("d MMM yyyy", CultureInfo.CurrentCulture)
                : $"{first:d MMM yyyy} to {last:d MMM yyyy}");
            var places = items.Select(_locations.Place).Where(p => p is not null).Distinct().ToList();
            if (places.Count > 0) Row("Places", string.Join("\n", places.Take(6)) + (places.Count > 6 ? $"\nand {places.Count - 6} more" : ""));
            if (videos > 0) Row("Video length", TimeSpan.FromMilliseconds(items.Where(i => i.IsVideo).Sum(i => i.DurationMs)).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture));
        }
        _table.ResumeLayout();
    }

    private async Task LoadDetailsAsync(MediaItem item)
    {
        try
        {
            var r = await AdbClient.Default.ShellAsync(_serial, [MediaDetails.QueryCommand(item)]);
            _details[item.Id] = MediaDetails.Parse(r.Output);
            if (!IsDisposed && _selection is [var still] && still.Id == item.Id) ShowSelection(_selection);
        }
        catch (Exception ex) { Log.Info("details: " + ex.Message); }
    }

    /// <summary>
    /// "Show on map": opens OpenStreetMap in the browser at this point. Only clicking it sends the coordinates
    /// anywhere; nothing is looked up online otherwise.
    /// </summary>
    private void MapLink(Core.Media.GeoPoint p)
    {
        var link = new LinkLabel { Text = "Show on map", AutoSize = true, Margin = new Padding(0, 4, 0, 0), LinkColor = Color.FromArgb(0x4C, 0xA3, 0xFF) };
        string lat = p.Latitude.ToString("0.#####", CultureInfo.InvariantCulture), lon = p.Longitude.ToString("0.#####", CultureInfo.InvariantCulture);
        link.LinkClicked += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            $"https://www.openstreetmap.org/?mlat={lat}&mlon={lon}#map=15/{lat}/{lon}") { UseShellExecute = true });
        _table.Controls.Add(new Label { AutoSize = true });
        _table.Controls.Add(link);
    }

    private void SetPreview(MediaItem? item)
    {
        var old = _preview.Image;
        _preview.Image = null;
        old?.Dispose();
        _preview.Visible = item is not null;
        if (item is null) return;
        if (_thumbs.CachedFile(item) is { } file)
        {
            try
            {
                using var src = Image.FromFile(file);
                _preview.Image = new Bitmap(src);
            }
            catch (Exception ex) when (ex is OutOfMemoryException or IOException) { }
        }
    }

    private void Row(string name, string value)
    {
        var label = new Label { Text = name, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 6, 8, 0) };
        var text = new Label { Text = value, AutoSize = true, MaximumSize = new Size(Width - (int)Math.Round(120 * DeviceDpi / 96.0), 0), Margin = new Padding(0, 6, 0, 0) };
        _table.Controls.Add(label);
        _table.Controls.Add(text);
        if (name.Length == 0) _table.SetColumnSpan(text, 2);
        if (name.Length == 0) _table.Controls.Remove(label);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Color.FromArgb(0x30, 0x30, 0x30));
        e.Graphics.DrawLine(pen, 0, 0, 0, Height);
    }
}
