using Songify.Core.Infrastructure;
using Songify.Core.Models;

namespace Songify.Tests;

public class SettingsTests
{
    [Fact]
    public void Theme_defaults_to_windows_for_older_settings_files_and_round_trips()
    {
        var dir = Directory.CreateTempSubdirectory("songify-settings-");
        try
        {
            var file = Path.Combine(dir.FullName, "settings.json");
            File.WriteAllText(file, """{ "Format": "Flac" }"""); // written before the theme setting existed
            Assert.Equal(AppTheme.System, JsonFile.Load<AppSettings>(file)!.Theme);

            JsonFile.Save(file, new AppSettings { Theme = AppTheme.Dark });
            Assert.Equal(AppTheme.Dark, JsonFile.Load<AppSettings>(file)!.Theme);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Sanitize_resets_enum_values_a_hand_edited_file_made_invalid()
    {
        var s = new AppSettings { Format = (AudioFormat)99, Theme = (AppTheme)7, BitrateKbps = 192 }.Sanitize();

        Assert.Equal(new AppSettings().Format, s.Format);
        Assert.Equal(AppTheme.System, s.Theme);
        Assert.Equal(192, s.BitrateKbps);
    }
}
