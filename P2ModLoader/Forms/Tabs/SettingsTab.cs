using P2ModLoader.Abstract;
using P2ModLoader.Helper;

namespace P2ModLoader.Forms.Tabs;

public class SettingsTab : BaseTab {
    private TextBox? _pathTextBox;
    private Button? _browseButton, _locateButton;
    private CheckBox? _allowConflictsCheckBox;
    private MainForm? _mainForm;

    public SettingsTab(TabPage page, MainForm mainForm) : base(page) {
        _mainForm = mainForm;
        InitializeComponents();
    }
    
    protected sealed override void InitializeComponents() {
        var pathLabel = new Label();
        pathLabel.Text = "Installation Path:";
        pathLabel.Location = new Point(20, 20);
        pathLabel.AutoSize = true;

        _pathTextBox = new TextBox();
        _pathTextBox.Location = new Point(20, 45);
        _pathTextBox.Width = 400;
        _pathTextBox.Height = 28;

        _pathTextBox!.Text = SettingsHolder.InstallPath ?? string.Empty;
        SettingsHolder.InstallPathChanged += () => {
            _pathTextBox!.Text = SettingsHolder.InstallPath ?? string.Empty;
        };
        
        _browseButton = new Button();
        _browseButton.Text = "Browse";
        _browseButton.Location = new Point(430, 45);
        _browseButton.Width = 80;
        _browseButton.Height = 32;
        _browseButton.Click += BrowseButton_Click;

        _locateButton = new Button();
        _locateButton.Text = "Locate";
        _locateButton.Location = new Point(520, 45);
        _locateButton.Width = 80;
        _locateButton.Height = 32;
        _locateButton.Click += LocateButton_Click;

        _allowConflictsCheckBox = new CheckBox();
        _allowConflictsCheckBox.Text = "Allow startup with conflicts (not recommended)";
        _allowConflictsCheckBox.Location = new Point(20, 85);
        _allowConflictsCheckBox.AutoSize = true;
        _allowConflictsCheckBox.Checked = SettingsHolder.AllowStartupWithConflicts;
        _allowConflictsCheckBox.CheckedChanged += AllowConflictsCheckBox_CheckedChanged;

        Tab.Controls.AddRange([
            pathLabel,
            _pathTextBox,
            _browseButton,
            _locateButton,
            _allowConflictsCheckBox
        ]);
    }

    private void BrowseButton_Click(object? sender, EventArgs e) {
        using var folderDialog = new FolderBrowserDialog();
        if (folderDialog.ShowDialog() != DialogResult.OK) return;
        SettingsHolder.InstallPath = folderDialog.SelectedPath;
        _mainForm.UpdateControls();
    }

    private void LocateButton_Click(object? sender, EventArgs e) {
        var installPath = InstallationLocator.FindInstall();
        if (string.IsNullOrEmpty(installPath)) return;
        SettingsHolder.InstallPath = installPath;
        _mainForm.UpdateControls();
    }
    
    private void AllowConflictsCheckBox_CheckedChanged(object? sender, EventArgs e) {
        SettingsHolder.AllowStartupWithConflicts = _allowConflictsCheckBox!.Checked;
    }
}