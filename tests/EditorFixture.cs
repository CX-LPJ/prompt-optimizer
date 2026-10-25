using System;
using System.Drawing;
using System.Windows.Forms;
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]
public static class EditorFixture {
    [STAThread] public static void Main() {
        Application.EnableVisualStyles();
        var form = new Form { Text = "Prompt Optimizer External Test", Size = new Size(650, 520), StartPosition = FormStartPosition.CenterScreen };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Padding = new Padding(16) };
        layout.Controls.Add(new Label { Text = "Plain text", AutoSize = true });
        layout.Controls.Add(new TextBox { Name = "plain", Text = "Keep 0.5 mM; do not heat.", Multiline = true, Dock = DockStyle.Fill, Height = 80 });
        layout.Controls.Add(new Label { Text = "Password test", AutoSize = true });
        layout.Controls.Add(new TextBox { Name = "password", Text = "LOCAL_TEST_PASSWORD", UseSystemPasswordChar = true, Dock = DockStyle.Fill });
        layout.Controls.Add(new Label { Text = "Read only", AutoSize = true });
        layout.Controls.Add(new TextBox { Name = "readonly", Text = "READ_ONLY_TEST", ReadOnly = true, Dock = DockStyle.Fill });
        layout.Controls.Add(new Label { Text = "Rich text preview", AutoSize = true });
        var rich = new RichTextBox { Name = "rich", Text = "Keep all 5 samples unchanged.", Dock = DockStyle.Fill };
        rich.Select(0, 4); rich.SelectionColor = Color.Blue; rich.SelectionLength = 0; layout.Controls.Add(rich);
        for (int i = 0; i < 8; i++) layout.RowStyles.Add(new RowStyle(i % 2 == 0 ? SizeType.Absolute : SizeType.Percent, i % 2 == 0 ? 28 : 25));
        form.Controls.Add(layout); Application.Run(form);
    }
}
