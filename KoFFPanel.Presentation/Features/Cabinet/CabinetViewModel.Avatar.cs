using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KoFFPanel.Domain.Entities;
using CommunityToolkit.Mvvm.Input;

namespace KoFFPanel.Presentation.Features.Cabinet;

public partial class CabinetViewModel
{
    private void LoadAvatarRegistry()
    {
        try
        {
            if (File.Exists(_avatarsRegistryPath))
            {
                string json = File.ReadAllText(_avatarsRegistryPath);
                _avatarRegistry = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            }
        }
        catch { }
    }

    private void SaveAvatarRegistry()
    {
        try
        {
            string dir = Path.GetDirectoryName(_avatarsRegistryPath)!;
            Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(_avatarRegistry);
            File.WriteAllText(_avatarsRegistryPath, json);
        }
        catch { }
    }

    [RelayCommand]
    private async Task ChangeAvatarAsync(VpnClient? client)
    {
        if (client == null) return;

        var openFileDialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите аватар (Изображение будет автоматически сжато)",
            Filter = "Изображения|*.jpg;*.jpeg;*.png;*.bmp|Все файлы|*.*"
        };

        if (openFileDialog.ShowDialog() != true) return;

        try
        {
            string sourcePath = openFileDialog.FileName;
            string avatarsFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Avatars");
            Directory.CreateDirectory(avatarsFolder);

            string destFileName = $"avatar_{client.Email}_{Guid.NewGuid().ToString("N").Substring(0, 8)}.jpg";
            string destPath = Path.Combine(avatarsFolder, destFileName);

            await Task.Run(() =>
            {
                var originalImage = new BitmapImage();
                originalImage.BeginInit();
                originalImage.UriSource = new Uri(sourcePath);
                originalImage.CacheOption = BitmapCacheOption.OnLoad;
                originalImage.EndInit();
                originalImage.Freeze();

                int size = Math.Min(originalImage.PixelWidth, originalImage.PixelHeight);
                int x = (originalImage.PixelWidth - size) / 2;
                int y = (originalImage.PixelHeight - size) / 2;

                var croppedBitmap = new CroppedBitmap(originalImage, new System.Windows.Int32Rect(x, y, size, size));
                var scaledBitmap = new TransformedBitmap(croppedBitmap, new ScaleTransform(64.0 / size, 64.0 / size));

                var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
                encoder.Frames.Add(BitmapFrame.Create(scaledBitmap));

                using var fileStream = new FileStream(destPath, FileMode.Create);
                encoder.Save(fileStream);
            });

            client.AvatarPath = destPath;

            if (!string.IsNullOrEmpty(client.Email))
            {
                _avatarRegistry[client.Email] = destPath;
                SaveAvatarRegistry();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Ошибка при сжатии аватара: {ex.Message}");
        }
    }
}
