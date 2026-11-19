using QRCoder;
using Streamy.Core.Adb;
using Streamy.Services;

namespace Streamy.UI;

/// <summary>
/// Shows a QR code for Android 11+'s "Pair device with QR code", waits for the phone to advertise it over mDNS,
/// then pairs and lets adb connect. Closes with OK once the phone is paired.
/// </summary>
internal sealed class QrPairDialog : Form
{
    private readonly WirelessPairing _pairing = WirelessPairing.Create();
    private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Bottom, Height = 48, TextAlign = ContentAlignment.MiddleCenter };
    private readonly CancellationTokenSource _cts = new();

    public string? PairedAddress { get; private set; }

    public QrPairDialog()
    {
        Text = "Pair with QR code";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(380, 470);
        BackColor = Color.White;
        ForeColor = Color.Black;

        var help = new Label
        {
            Dock = DockStyle.Top,
            Height = 70,
            Padding = new Padding(12, 10, 12, 0),
            Text = "On the phone: Settings > Developer options > Wireless debugging > Pair device with QR code, " +
                   "then scan this code. The phone and this PC must be on the same Wi-Fi.",
        };
        var qr = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.CenterImage, Image = CreateQr(_pairing.QrText) };
        Controls.Add(qr);
        Controls.Add(help);
        Controls.Add(_status);
        _status.Text = "Waiting for the phone to scan the code";
        Shown += async (_, _) => await WaitAndPairAsync();
    }

    private static Bitmap CreateQr(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);
        using var ms = new MemoryStream(png);
        return new Bitmap(ms);
    }

    private async Task WaitAndPairAsync()
    {
        var adb = AdbClient.Default;
        try
        {
            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (DateTime.UtcNow < deadline)
            {
                var service = _pairing.FindService(await adb.MdnsServicesAsync(_cts.Token));
                if (service is not null)
                {
                    _status.Text = "Pairing";
                    var r = await adb.PairAsync(service.Address, _pairing.Password, _cts.Token);
                    if (!AdbParsers.IsPairSuccess(r.All))
                    {
                        _status.Text = "Pairing failed: " + r.All.Trim();
                        return;
                    }
                    PairedAddress = service.Address;
                    _status.Text = "Paired.";
                    DialogResult = DialogResult.OK;
                    return;
                }
                await Task.Delay(1000, _cts.Token);
            }
            _status.Text = "The phone did not show up. Check that it is on the same Wi-Fi, then try again.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, "QR pairing");
            if (!IsDisposed) _status.Text = "Pairing failed: " + ex.Message;
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _cts.Cancel();
        base.OnFormClosed(e);
    }
}
