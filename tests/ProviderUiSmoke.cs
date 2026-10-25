using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using PromptQuick;

public static class ProviderUiSmoke {
    static int exitCode;
    static T Field<T>(Control root, string name) where T : Control { return (T)root.Controls.Find(name, true).Single(); }
    static void Check(bool value, string name) { if (!value) throw new Exception(name); }
    static async Task Ready(Form dialog) {
        var save = Field<Button>(dialog, "saveProvider");
        for (int i = 0; i < 150 && !save.Enabled; i++) await Task.Delay(100);
        Check(save.Enabled, "Settings request did not finish");
    }
    static async Task Select(ProviderDialog dialog, string id) {
        var providers = Field<ComboBox>(dialog, "providerSelect");
        for (int i = 0; i < providers.Items.Count; i++) if (((ProviderProfile)providers.Items[i]).id == id) { providers.SelectedIndex = i; break; }
        await Ready(dialog);
    }
    [STAThread] public static int Main(string[] args) {
        string dir = args[0], endpoint = args[1], report = args[2];
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        try {
            var settings = new Settings();
            var store = new ProviderStore(dir, settings); var data = store.Copy();
            var a = data.providers.First(p => p.id == "compatible"); a.baseUrl = endpoint + "/a/v1"; a.model = "model-a"; a.SetApiKey("synthetic-a");
            var b = new ProviderProfile { id = "custom-b", name = "测试供应商 B", baseUrl = endpoint + "/b/v1", model = "model-b" }; b.SetApiKey("synthetic-b"); data.providers.Add(b);
            var manual = new ProviderProfile { id = "custom-manual", name = "手填模型服务", baseUrl = endpoint + "/manual/v1", model = "manual-model" }; manual.SetApiKey("synthetic-manual"); data.providers.Add(manual);
            data.selectedId = a.id; store.Save(data);
            using (var client = new AiClient(dir, settings, store)) using (var dialog = new ProviderDialog(store, client, () => {})) {
                dialog.Shown += async (s, e) => {
                    try {
                        await Ready(dialog);
                        var key = Field<TextBox>(dialog, "apiKey"); var models = Field<ComboBox>(dialog, "modelSelect"); var status = Field<Label>(dialog, "providerStatus");
                        Check(key.UseSystemPasswordChar && key.Text == "", "Saved API key must remain hidden and absent from textbox");
                        Check(models.Items.Contains("model-a"), "Provider A model list missing");
                        await Select(dialog, "openai");
                        Check(status.Text.Contains("配置地址"), "Unconfigured API should remain editable without a network wait");
                        await Select(dialog, "compatible");
                        Field<Button>(dialog, "addProvider").PerformClick();
                        Field<TextBox>(dialog, "providerName").Text = "新增测试服务";
                        Field<TextBox>(dialog, "baseUrl").Text = endpoint + "/new/v1";
                        key.Text = "synthetic-new"; models.Text = "manual-new";
                        Field<Button>(dialog, "testProvider").PerformClick(); await Ready(dialog);
                        Check(status.Text.Contains("连接成功"), "New custom provider was not callable");
                        await Select(dialog, "custom-manual");
                        Check(status.Text.Contains("手动"), "Unsupported model-list endpoint should explain manual entry");
                        Field<Button>(dialog, "testProvider").PerformClick(); await Ready(dialog);
                        Check(status.Text.Contains("连接成功"), "Manual model connection test failed");
                        await Select(dialog, "custom-b");
                        Check(models.Items.Contains("model-b") && !models.Items.Contains("model-a"), "Switch left stale provider model list");
                        models.Text = "manual-b";
                        key.Text = "synthetic-wrong"; Field<Button>(dialog, "testProvider").PerformClick(); await Ready(dialog);
                        Check(status.Text.Contains("认证失败"), "Authentication error missing from settings UI");
                        key.Text = "synthetic-new-b"; Field<Button>(dialog, "testProvider").PerformClick(); await Ready(dialog);
                        Check(status.Text.Contains("连接成功"), "Provider B replacement key/model did not reach API");
                        key.Clear();
                        using (var screenshot = new Bitmap(dialog.Width, dialog.Height)) { dialog.DrawToBitmap(screenshot, dialog.ClientRectangle); screenshot.Save(Path.Combine(dir, "provider-settings.png")); }
                        Field<Button>(dialog, "saveProvider").PerformClick();
                        Check(dialog.DialogResult == DialogResult.OK, "Save did not finish");
                    } catch (Exception error) { exitCode = 1; File.WriteAllText(report, Json.Encode(new { ok = false, error = error.ToString() })); dialog.Close(); }
                };
                Application.Run(dialog);
            }
            if (exitCode != 0) return exitCode;
            var reopened = new ProviderStore(dir, settings);
            Check(reopened.Current.id == "custom-b" && reopened.Current.model == "manual-b", "Provider and manual model did not survive reopening");
            Check(reopened.Current.GetApiKey() == "synthetic-new-b", "Replacement credential not restored");
            Check(!File.ReadAllText(Path.Combine(dir, "providers.json")).Contains("synthetic-"), "Plaintext key written to disk");
            using (var client = new AiClient(dir, settings, reopened)) {
                var response = client.Request("optimize", "请保留0.5 mM。").GetAwaiter().GetResult();
                Check(Json.Str(response, "result").Contains("provider-b") && Json.Str(response, "result").Contains("0.5 mM"), "Saved provider not used by normal optimize request");
                response = client.Request("translate", "保持0.5 mM。", "english").GetAwaiter().GetResult();
                Check(Json.Str(response, "result") == "Keep 0.5 mM.", "Normal translate request did not use selected API");
            }
            File.WriteAllText(report, Json.Encode(new { ok = true, customProvider = true, providerSwitch = true, modelLists = true, manualModel = true, maskedKey = true, encryptedKey = true, authenticationError = true, testConnection = true, saveAndReopen = true, optimizeAndTranslate = true }));
            return 0;
        } catch (Exception error) { File.WriteAllText(report, Json.Encode(new { ok = false, error = error.ToString() })); return 1; }
    }
}
