using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using MahApps.Metro.Controls;
using SpellEditor.Sources.BLP;
using SpellEditor.Sources.Config;
using SpellEditor.Sources.Database;
using SpellEditor.Sources.DBC;

namespace SpellEditor
{
    /// <summary>
    /// Visual + tabular editor for the custom retail-style talent/specialization system
    /// (aaa_custom_spell_tab for spec-card metadata, and the aa_vista_talent_slots view -
    /// backed by aa_talent_slot / aa_specialization / aa_talents - for the talent tree itself).
    ///
    /// aa_vista_talent_slots is a VIEW joining three tables:
    ///   aa_specialization (sp): ID, Class, Specialization, Comment
    ///   aa_talents        (t):  ID, SpellRank1..5, Talent_Type, Comment
    ///   aa_talent_slot    (ts): ID, IDSpec, IDTalent, Specialization_Type, row, column,
    ///                           Requiredspell1..9, Arrow1..9, MinumumRequiredTalents,
    ///                           MinimumRequiredLevel, MinimumSpecificRequired, Comment
    /// A 3-table joined view like this generally isn't directly UPDATE/DELETE-able in MySQL,
    /// so all writes below target the three base tables directly via ts.ID / IDSpec / IDTalent
    /// (read from the view, which is fine to SELECT from).
    /// </summary>
    public partial class TalentTreeEditorWindow : MetroWindow
    {
        private readonly IDatabaseAdapter _adapter; // main spell.dbc connection, kept for potential future cross-referencing - not used for talent CRUD
        private IDatabaseAdapter _talentsAdapter;   // separate connection for aaa_custom_spell_tab / aa_specialization / aa_talents / aa_talent_slot

        private const double CellWidth = 90;
        private const double CellHeight = 90;

        private readonly ObservableCollection<SpecRow> _specs = new ObservableCollection<SpecRow>();
        private readonly ObservableCollection<TalentSlotRow> _talents = new ObservableCollection<TalentSlotRow>();

        private SpecRow _selectedSpec;
        private TalentSlotRow _selectedTalent;

        // Caches so hovering/re-rendering doesn't re-query the spell DB for the same ID repeatedly
        private readonly Dictionary<int, string> _spellNameCache = new Dictionary<int, string>();
        private readonly Dictionary<int, ImageSource> _spellIconCache = new Dictionary<int, ImageSource>();

        public TalentTreeEditorWindow(IDatabaseAdapter adapter)
        {
            _adapter = adapter;
            InitializeComponent();
            Closed += (s, e) => _talentsAdapter?.Dispose();
        }

        private string SelectedClass => ClassComboBox.SelectedItem as string;

        private void _Loaded(object sender, RoutedEventArgs e)
        {
            // Pre-fill from the last-used talents connection, if any; otherwise default the
            // host/port/user to the main spell.dbc connection's as a convenience (same server,
            // different database, is the common case) without assuming the database name.
            TalentsHostTextBox.Text = string.IsNullOrEmpty(Config.TalentsHost) ? Config.Host : Config.TalentsHost;
            TalentsPortTextBox.Text = string.IsNullOrEmpty(Config.TalentsPort) ? Config.Port : Config.TalentsPort;
            TalentsUserTextBox.Text = string.IsNullOrEmpty(Config.TalentsUser) ? Config.User : Config.TalentsUser;
            TalentsPassBox.Password = string.IsNullOrEmpty(Config.TalentsPass) ? Config.Pass : Config.TalentsPass;
            TalentsDatabaseTextBox.Text = Config.TalentsDatabase;

            ClassComboBox.ItemsSource = new[]
            {
                "WARRIOR", "PALADIN", "HUNTER", "ROGUE", "PRIEST",
                "DEATHKNIGHT", "SHAMAN", "MAGE", "WARLOCK", "DRUID"
            };
            ClassComboBox.SelectedIndex = 0;

            if (!string.IsNullOrEmpty(TalentsDatabaseTextBox.Text))
                ConnectTalents();
        }

        private void ConnectTalentsButton_Click(object sender, RoutedEventArgs e) => ConnectTalents();

        private void ConnectTalents()
        {
            if (string.IsNullOrWhiteSpace(TalentsDatabaseTextBox.Text))
            {
                ShowError("Cannot connect", new Exception("Enter a database name for the talents connection first."));
                return;
            }

            try
            {
                _talentsAdapter?.Dispose();
                _talentsAdapter = new MySQL(
                    TalentsHostTextBox.Text.Trim(),
                    TalentsPortTextBox.Text.Trim(),
                    TalentsUserTextBox.Text.Trim(),
                    TalentsPassBox.Password,
                    TalentsDatabaseTextBox.Text.Trim());

                Config.TalentsHost = TalentsHostTextBox.Text.Trim();
                Config.TalentsPort = TalentsPortTextBox.Text.Trim();
                Config.TalentsUser = TalentsUserTextBox.Text.Trim();
                Config.TalentsPass = TalentsPassBox.Password;
                Config.TalentsDatabase = TalentsDatabaseTextBox.Text.Trim();

                TalentsConnectionStatus.Text = "Connected to " + TalentsDatabaseTextBox.Text.Trim();
                TalentsConnectionStatus.Foreground = Brushes.LightGreen;

                LoadClass();
            }
            catch (Exception ex)
            {
                _talentsAdapter = null;
                TalentsConnectionStatus.Text = "Not connected";
                TalentsConnectionStatus.Foreground = Brushes.Gray;
                ShowError("Failed to connect to the talents database", ex);
            }
        }

        #region Loading

        private void ReloadButton_Click(object sender, RoutedEventArgs e) => LoadClass();

        private void ClassComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => LoadClass();

        private void LoadClass()
        {
            if (string.IsNullOrEmpty(SelectedClass) || _talentsAdapter == null)
                return;

            try
            {
                LoadSpecs();
            }
            catch (Exception ex)
            {
                ShowError("Failed to load specializations", ex);
            }
        }

        private void LoadSpecs()
        {
            _specs.Clear();
            var table = _talentsAdapter.Query(
                "SELECT Class, Id, Name, TP, MaxActiveSpells, Icon, Background, SpecSpell, Description, " +
                "Spell1, Spell2, Spell3, Spell4, Spell5, Spell6, Role1, Role2, Stat, " +
                "BackgroundContentRight, BackgroundContentBottom, BackgroundVisibleWidth " +
                $"FROM `aaa_custom_spell_tab` WHERE Class = '{_talentsAdapter.EscapeString(SelectedClass)}' ORDER BY Id;");

            foreach (DataRow row in table.Rows)
                _specs.Add(SpecRow.FromDataRow(row));

            SpecListBox.ItemsSource = null;
            SpecListBox.ItemsSource = _specs;

            if (_specs.Count > 0)
                SpecListBox.SelectedIndex = 0;
            else
            {
                _selectedSpec = null;
                RenderSpecEditor();
                _talents.Clear();
                RenderTreeCanvas();
            }
        }

        private void SpecListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selectedSpec = SpecListBox.SelectedItem as SpecRow;
            RenderSpecEditor();
            LoadTalentsForSpec();
        }

        private void LoadTalentsForSpec()
        {
            _talents.Clear();
            _selectedTalent = null;
            RenderTalentEditor();

            if (_selectedSpec == null || _talentsAdapter == null)
            {
                RenderTreeCanvas();
                return;
            }

            try
            {
                // Reading from the view is fine - only writes need to target the base tables.
                // "Specialization" here is the per-class spec number (1, 2, 3...), the same
                // convention aaa_custom_spell_tab.Id uses - the two aren't foreign-keyed to
                // each other, just kept in sync by convention.
                var table = _talentsAdapter.Query(
                    "SELECT SlotID, IDSpec, IDTalent, Class, Specialization, " +
                    "SpellRank1, SpellRank2, SpellRank3, SpellRank4, SpellRank5, Talent_Type, " +
                    "Specialization_Type, `row`, `column`, " +
                    "Requiredspell1, Requiredspell2, Requiredspell3, Requiredspell4, Requiredspell5, " +
                    "Requiredspell6, Requiredspell7, Requiredspell8, Requiredspell9, " +
                    "Arrow1, Arrow2, Arrow3, Arrow4, Arrow5, Arrow6, Arrow7, Arrow8, Arrow9, " +
                    "MinumumRequiredTalents, MinimumRequiredLevel, MinimumSpecificRequired, " +
                    "Slot_Comment, Talent_Comment " +
                    $"FROM `aa_vista_talent_slots` " +
                    $"WHERE Class = '{_talentsAdapter.EscapeString(SelectedClass)}' AND Specialization = {_selectedSpec.Id};");

                foreach (DataRow row in table.Rows)
                    _talents.Add(TalentSlotRow.FromDataRow(row));
            }
            catch (Exception ex)
            {
                ShowError("Failed to load talents", ex);
            }

            RenderTreeCanvas();
        }

        /// <summary>Finds the aa_specialization row for the current Class + spec number, creating
        /// one if it doesn't exist yet, and returns its ID (aa_talent_slot.IDSpec).</summary>
        private int FindOrCreateSpecializationId()
        {
            var existing = _talentsAdapter.QuerySingleValue(
                $"SELECT ID FROM `aa_specialization` WHERE Class = '{_talentsAdapter.EscapeString(SelectedClass)}' " +
                $"AND Specialization = {_selectedSpec.Id};");
            if (existing != null && existing != DBNull.Value)
                return Convert.ToInt32(existing);

            _talentsAdapter.Execute(
                $"INSERT INTO `aa_specialization` (Class, Specialization, Comment) VALUES " +
                $"('{_talentsAdapter.EscapeString(SelectedClass)}', {_selectedSpec.Id}, '');");
            return Convert.ToInt32(_talentsAdapter.QuerySingleValue("SELECT LAST_INSERT_ID();"));
        }

        #endregion

        #region Tree canvas rendering

        private void RenderTreeCanvas()
        {
            TreeCanvas.Children.Clear();

            if (_talents.Count == 0)
                return;

            var maxRow = _talents.Max(t => t.Row);
            var maxCol = _talents.Max(t => t.Column);
            TreeCanvas.Width = Math.Max(900, (maxCol + 2) * CellWidth);
            TreeCanvas.Height = Math.Max(900, (maxRow + 2) * CellHeight);

            // Draw arrows first so talent boxes sit on top of them. Each Arrow1-9 string on a
            // slot encodes a direction + distance to another slot using N/S/E/W letters - each
            // letter is one full grid step, and the net displacement is just the sum of them
            // (so "SW" = 1 down + 1 left, "SSW" = 2 down + 1 left, etc). Requiredspell1-9 lines
            // up positionally with Arrow1-9 (interleaved in the real table), but the arrow's
            // direction/target is purely geometric - it doesn't matter here whether another
            // slot actually exists at the computed destination.
            foreach (var talent in _talents)
            {
                foreach (var arrow in talent.Arrow)
                {
                    if (TryGetArrowOffset(arrow, out var rowDelta, out var colDelta))
                    {
                        DrawArrow(talent.Row, talent.Column, talent.Row + rowDelta, talent.Column + colDelta);
                    }
                }
            }

            foreach (var talent in _talents)
            {
                var box = BuildTalentBox(talent);
                Canvas.SetLeft(box, talent.Column * CellWidth);
                Canvas.SetTop(box, talent.Row * CellHeight);
                TreeCanvas.Children.Add(box);
            }
        }

        /// <summary>Decodes an Arrow string (e.g. "N", "SW", "SSW") into a net (rowDelta, colDelta)
        /// grid offset. Returns false for empty/"0"/no-direction-letters values (no arrow).</summary>
        private static bool TryGetArrowOffset(string arrow, out int rowDelta, out int colDelta)
        {
            rowDelta = 0;
            colDelta = 0;
            if (string.IsNullOrWhiteSpace(arrow) || arrow.Trim() == "0")
                return false;

            foreach (var c in arrow.ToUpperInvariant())
            {
                switch (c)
                {
                    case 'N': rowDelta -= 1; break;
                    case 'S': rowDelta += 1; break;
                    case 'E': colDelta += 1; break;
                    case 'W': colDelta -= 1; break;
                        // any other character (spaces, punctuation) is ignored
                }
            }

            return rowDelta != 0 || colDelta != 0;
        }

        private void DrawArrow(int fromRow, int fromCol, int toRow, int toCol)
        {
            var line = new Line
            {
                X1 = fromCol * CellWidth + CellWidth / 2,
                Y1 = fromRow * CellHeight + CellHeight / 2,
                X2 = toCol * CellWidth + CellWidth / 2,
                Y2 = toRow * CellHeight + CellHeight / 2,
                Stroke = Brushes.Goldenrod,
                StrokeThickness = 2,
                Opacity = 0.75
            };
            TreeCanvas.Children.Add(line);
        }

        private Border BuildTalentBox(TalentSlotRow talent)
        {
            var isSelected = talent == _selectedTalent;

            var border = new Border
            {
                Width = CellWidth - 14,
                Height = CellHeight - 14,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(isSelected ? 3 : 1),
                BorderBrush = isSelected ? Brushes.Gold : Brushes.Gray,
                Background = new SolidColorBrush(ColorForTalentType(talent.TalentType)),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = talent,
                ToolTip = BuildTalentTooltip(talent)
            };

            var stack = new StackPanel { Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center };

            if (ShowIconsCheckBox.IsChecked == true)
            {
                var iconRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                var isChoice = string.Equals(talent.TalentType, "choice", StringComparison.OrdinalIgnoreCase);
                // Choice nodes show both options side by side; anything else (rank/passive/spell)
                // only shows the icon for the first rank.
                var iconSpellIds = isChoice
                    ? new[] { talent.SpellRank1, talent.SpellRank2 }.Where(id => id > 0)
                    : new[] { talent.SpellRank1 }.Where(id => id > 0);

                foreach (var spellId in iconSpellIds)
                {
                    var icon = GetSpellIcon(spellId);
                    if (icon != null)
                    {
                        iconRow.Children.Add(new Image { Source = icon, Width = 24, Height = 24, Margin = new Thickness(1) });
                    }
                }
                if (iconRow.Children.Count > 0)
                    stack.Children.Add(iconRow);
            }

            stack.Children.Add(new TextBlock
            {
                Text = talent.TalentType,
                FontSize = 9,
                Foreground = Brushes.LightGray,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            stack.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(talent.SlotComment) ? talent.SpellRank1.ToString(CultureInfo.InvariantCulture) : talent.SlotComment,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            border.Child = stack;

            border.MouseLeftButtonUp += (s, e) =>
            {
                _selectedTalent = talent;
                RenderTalentEditor();
                RenderTreeCanvas();
            };

            return border;
        }

        /// <summary>Tooltip text: the spell ID(s) this slot provides (i.e. what another slot's
        /// Requiredspell1-9 would reference to depend on this one) plus each one's resolved name.</summary>
        private string BuildTalentTooltip(TalentSlotRow talent)
        {
            var lines = new List<string> { $"Slot ID: {talent.SlotID}" };
            var spellIds = new[] { talent.SpellRank1, talent.SpellRank2, talent.SpellRank3, talent.SpellRank4, talent.SpellRank5 }
                .Where(id => id > 0).ToList();

            if (spellIds.Count == 0)
            {
                lines.Add("No spells assigned.");
            }
            else
            {
                foreach (var spellId in spellIds)
                {
                    lines.Add($"{spellId} - {GetSpellName(spellId)}");
                }
            }

            return string.Join("\n", lines);
        }

        private void ShowIconsCheckBox_Changed(object sender, RoutedEventArgs e) => RenderTreeCanvas();

        private static Color ColorForTalentType(string talentType)
        {
            switch ((talentType ?? "").ToLowerInvariant())
            {
                case "choice": return Color.FromRgb(0x6A, 0x3D, 0x9A);
                case "rank": return Color.FromRgb(0x2E, 0x6B, 0x9E);
                case "passive": return Color.FromRgb(0x3B, 0x7A, 0x3B);
                case "spell": return Color.FromRgb(0x9E, 0x7A, 0x2E);
                default: return Color.FromRgb(0x55, 0x55, 0x55);
            }
        }

        #endregion

        #region Specialization editor (aaa_custom_spell_tab - spec CARD metadata, not aa_specialization)

        private void RenderSpecEditor()
        {
            SpecEditorPanel.Children.Clear();
            DeleteSpecButton.IsEnabled = _selectedSpec != null;

            if (_selectedSpec == null)
            {
                SpecEditorPanel.Children.Add(new TextBlock
                {
                    Text = "No specialization selected.",
                    Foreground = Brushes.Gray,
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            var s = _selectedSpec;
            AddField(SpecEditorPanel, "Id", () => s.Id.ToString(), v => s.Id = ParseInt(v, s.Id));
            AddField(SpecEditorPanel, "Name", () => s.Name, v => s.Name = v);
            AddField(SpecEditorPanel, "Icon", () => s.Icon, v => s.Icon = v);
            AddField(SpecEditorPanel, "Background", () => s.Background, v => s.Background = v);
            AddField(SpecEditorPanel, "SpecSpell", () => s.SpecSpell.ToString(), v => s.SpecSpell = ParseInt(v, s.SpecSpell));
            AddField(SpecEditorPanel, "Description", () => s.Description, v => s.Description = v, multiline: true);
            AddField(SpecEditorPanel, "Spell1", () => s.Spell1.ToString(), v => s.Spell1 = ParseInt(v, s.Spell1));
            AddField(SpecEditorPanel, "Spell2", () => s.Spell2.ToString(), v => s.Spell2 = ParseInt(v, s.Spell2));
            AddField(SpecEditorPanel, "Spell3", () => s.Spell3.ToString(), v => s.Spell3 = ParseInt(v, s.Spell3));
            AddField(SpecEditorPanel, "Spell4", () => s.Spell4.ToString(), v => s.Spell4 = ParseInt(v, s.Spell4));
            AddField(SpecEditorPanel, "Spell5", () => s.Spell5.ToString(), v => s.Spell5 = ParseInt(v, s.Spell5));
            AddField(SpecEditorPanel, "Spell6", () => s.Spell6.ToString(), v => s.Spell6 = ParseInt(v, s.Spell6));
            AddField(SpecEditorPanel, "Role1 (0=none 1=DPS 2=Tank 3=Heal)", () => s.Role1.ToString(), v => s.Role1 = ParseInt(v, s.Role1));
            AddField(SpecEditorPanel, "Role2", () => s.Role2.ToString(), v => s.Role2 = ParseInt(v, s.Role2));
            AddField(SpecEditorPanel, "Stat", () => s.Stat, v => s.Stat = v);
            AddField(SpecEditorPanel, "BackgroundContentRight", () => s.BackgroundContentRight.ToString(CultureInfo.InvariantCulture), v => s.BackgroundContentRight = ParseFloat(v, s.BackgroundContentRight));
            AddField(SpecEditorPanel, "BackgroundContentBottom", () => s.BackgroundContentBottom.ToString(CultureInfo.InvariantCulture), v => s.BackgroundContentBottom = ParseFloat(v, s.BackgroundContentBottom));
            AddField(SpecEditorPanel, "BackgroundVisibleWidth", () => s.BackgroundVisibleWidth.ToString(CultureInfo.InvariantCulture), v => s.BackgroundVisibleWidth = ParseFloat(v, s.BackgroundVisibleWidth));

            var saveButton = new Button { Content = "Save Specialization", Margin = new Thickness(0, 8, 0, 0) };
            saveButton.Click += (s2, e2) => SaveSpec(s);
            SpecEditorPanel.Children.Add(saveButton);
        }

        private void SaveSpec(SpecRow s)
        {
            try
            {
                var sql =
                    "UPDATE `aaa_custom_spell_tab` SET " +
                    $"Name = '{_talentsAdapter.EscapeString(s.Name)}', " +
                    $"Icon = '{_talentsAdapter.EscapeString(s.Icon)}', " +
                    $"Background = '{_talentsAdapter.EscapeString(s.Background)}', " +
                    $"SpecSpell = {s.SpecSpell}, " +
                    $"Description = '{_talentsAdapter.EscapeString(s.Description)}', " +
                    $"Spell1 = {s.Spell1}, Spell2 = {s.Spell2}, Spell3 = {s.Spell3}, " +
                    $"Spell4 = {s.Spell4}, Spell5 = {s.Spell5}, Spell6 = {s.Spell6}, " +
                    $"Role1 = {s.Role1}, Role2 = {s.Role2}, " +
                    $"Stat = '{_talentsAdapter.EscapeString(s.Stat)}', " +
                    $"BackgroundContentRight = {s.BackgroundContentRight.ToString(CultureInfo.InvariantCulture)}, " +
                    $"BackgroundContentBottom = {s.BackgroundContentBottom.ToString(CultureInfo.InvariantCulture)}, " +
                    $"BackgroundVisibleWidth = {s.BackgroundVisibleWidth.ToString(CultureInfo.InvariantCulture)} " +
                    $"WHERE Class = '{_talentsAdapter.EscapeString(SelectedClass)}' AND Id = {s.Id};";
                _talentsAdapter.Execute(sql);
                LoadSpecs();
            }
            catch (Exception ex)
            {
                ShowError("Failed to save specialization", ex);
            }
        }

        private void AddSpecButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(SelectedClass) || _talentsAdapter == null) return;

            try
            {
                var nextId = (_specs.Count == 0) ? 1 : _specs.Max(x => x.Id) + 1;
                _talentsAdapter.Execute(
                    $"INSERT INTO `aaa_custom_spell_tab` " +
                    "(Class, Id, Name, TP, MaxActiveSpells, Icon, Background, SpecSpell, Description, " +
                    "Spell1, Spell2, Spell3, Spell4, Spell5, Spell6, Role1, Role2, Stat) VALUES (" +
                    $"'{_talentsAdapter.EscapeString(SelectedClass)}', {nextId}, 'New Spec', 0, 1, '', '', 0, ''," +
                    " 0, 0, 0, 0, 0, 0, 0, 0, '');");
                LoadSpecs();
            }
            catch (Exception ex)
            {
                ShowError("Failed to create specialization", ex);
            }
        }

        private void DeleteSpecButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedSpec == null) return;
            if (MessageBox.Show(this, $"Delete specialization '{_selectedSpec.Name}' and ALL its talents? This cannot be undone.",
                    "Confirm delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            try
            {
                // aa_vista_talent_slots is a joined view - can't DELETE from it directly.
                // Delete the underlying aa_talent_slot / aa_talents rows first (using the IDs
                // read via the view), then the aa_specialization row, then the card metadata row.
                foreach (var t in _talents.ToList())
                {
                    _talentsAdapter.Execute($"DELETE FROM `aa_talent_slot` WHERE ID = {t.SlotID};");
                    _talentsAdapter.Execute($"DELETE FROM `aa_talents` WHERE ID = {t.IDTalent};");
                }
                _talentsAdapter.Execute(
                    $"DELETE FROM `aa_specialization` WHERE Class = '{_talentsAdapter.EscapeString(SelectedClass)}' " +
                    $"AND Specialization = {_selectedSpec.Id};");
                _talentsAdapter.Execute(
                    $"DELETE FROM `aaa_custom_spell_tab` WHERE Class = '{_talentsAdapter.EscapeString(SelectedClass)}' " +
                    $"AND Id = {_selectedSpec.Id};");
                LoadSpecs();
            }
            catch (Exception ex)
            {
                ShowError("Failed to delete specialization", ex);
            }
        }

        #endregion

        #region Talent slot editor (aa_talent_slot + aa_talents, read via the aa_vista_talent_slots view)

        private void RenderTalentEditor()
        {
            TalentEditorPanel.Children.Clear();
            DeleteTalentButton.IsEnabled = _selectedTalent != null;

            if (_selectedTalent == null)
            {
                TalentEditorPanel.Children.Add(new TextBlock
                {
                    Text = "Click a talent on the tree to edit it.",
                    Foreground = Brushes.Gray,
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            var t = _selectedTalent;
            TalentEditorPanel.Children.Add(new TextBlock
            {
                Text = $"Slot ID: {t.SlotID}",
                Margin = new Thickness(0, 0, 0, 6),
                FontSize = 11,
                FontStyle = FontStyles.Italic,
                Foreground = Brushes.Silver
            });
            AddField(TalentEditorPanel, "Comment (talent slot label)", () => t.SlotComment, v => t.SlotComment = v);
            AddField(TalentEditorPanel, "Talent Comment (talent definition label)", () => t.TalentComment, v => t.TalentComment = v);
            AddField(TalentEditorPanel, "SpellRank1", () => t.SpellRank1.ToString(), v => t.SpellRank1 = ParseInt(v, t.SpellRank1));
            AddField(TalentEditorPanel, "SpellRank2 (choice option / rank 2)", () => t.SpellRank2.ToString(), v => t.SpellRank2 = ParseInt(v, t.SpellRank2));
            AddField(TalentEditorPanel, "SpellRank3", () => t.SpellRank3.ToString(), v => t.SpellRank3 = ParseInt(v, t.SpellRank3));
            AddField(TalentEditorPanel, "SpellRank4", () => t.SpellRank4.ToString(), v => t.SpellRank4 = ParseInt(v, t.SpellRank4));
            AddField(TalentEditorPanel, "SpellRank5", () => t.SpellRank5.ToString(), v => t.SpellRank5 = ParseInt(v, t.SpellRank5));
            AddField(TalentEditorPanel, "Talent_Type (rank/choice/passive/...)", () => t.TalentType, v => t.TalentType = v);
            AddField(TalentEditorPanel, "Specialization_Type", () => t.SpecializationType, v => t.SpecializationType = v);
            AddField(TalentEditorPanel, "Row", () => t.Row.ToString(), v => t.Row = ParseInt(v, t.Row));
            AddField(TalentEditorPanel, "Column", () => t.Column.ToString(), v => t.Column = ParseInt(v, t.Column));
            for (int i = 0; i < 9; i++)
            {
                var idx = i;
                AddField(TalentEditorPanel, $"Requiredspell{idx + 1}", () => t.RequiredSpell[idx].ToString(), v => t.RequiredSpell[idx] = ParseInt(v, t.RequiredSpell[idx]));
            }
            for (int i = 0; i < 9; i++)
            {
                var idx = i;
                AddField(TalentEditorPanel, $"Arrow{idx + 1}", () => t.Arrow[idx], v => t.Arrow[idx] = v);
            }
            AddField(TalentEditorPanel, "MinumumRequiredTalents", () => t.MinReqTal.ToString(), v => t.MinReqTal = ParseInt(v, t.MinReqTal));
            AddField(TalentEditorPanel, "MinimumRequiredLevel", () => t.MinReqLvl.ToString(), v => t.MinReqLvl = ParseInt(v, t.MinReqLvl));
            AddField(TalentEditorPanel, "MinimumSpecificRequired", () => t.MinReqSpe.ToString(), v => t.MinReqSpe = ParseInt(v, t.MinReqSpe));

            var saveButton = new Button { Content = "Save Talent", Margin = new Thickness(0, 8, 0, 0) };
            saveButton.Click += (s2, e2) => SaveTalent(t);
            TalentEditorPanel.Children.Add(saveButton);
        }

        private void SaveTalent(TalentSlotRow t)
        {
            try
            {
                // aa_talents: the spell-rank data
                _talentsAdapter.Execute(
                    $"UPDATE `aa_talents` SET " +
                    $"SpellRank1 = {t.SpellRank1}, SpellRank2 = {t.SpellRank2}, SpellRank3 = {t.SpellRank3}, " +
                    $"SpellRank4 = {t.SpellRank4}, SpellRank5 = {t.SpellRank5}, " +
                    $"Talent_Type = '{_talentsAdapter.EscapeString(t.TalentType)}', " +
                    $"Comment = '{_talentsAdapter.EscapeString(t.TalentComment)}' " +
                    $"WHERE ID = {t.IDTalent};");

                // aa_talent_slot: position, prerequisites, arrows, requirements
                var setClauses = new List<string>
                {
                    $"Comment = '{_talentsAdapter.EscapeString(t.SlotComment)}'",
                    $"Specialization_Type = '{_talentsAdapter.EscapeString(t.SpecializationType)}'",
                    $"`row` = {t.Row}", $"`column` = {t.Column}",
                    $"MinumumRequiredTalents = {t.MinReqTal}",
                    $"MinimumRequiredLevel = {t.MinReqLvl}",
                    $"MinimumSpecificRequired = {t.MinReqSpe}"
                };
                for (int i = 0; i < 9; i++)
                    setClauses.Add($"Requiredspell{i + 1} = {t.RequiredSpell[i]}");
                for (int i = 0; i < 9; i++)
                    setClauses.Add($"Arrow{i + 1} = '{_talentsAdapter.EscapeString(t.Arrow[i])}'");

                _talentsAdapter.Execute(
                    $"UPDATE `aa_talent_slot` SET {string.Join(", ", setClauses)} WHERE ID = {t.SlotID};");

                LoadTalentsForSpec();
            }
            catch (Exception ex)
            {
                ShowError("Failed to save talent", ex);
            }
        }

        private void AddTalentButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedSpec == null || _talentsAdapter == null) return;

            try
            {
                var idSpec = FindOrCreateSpecializationId();
                var nextRow = _talents.Count == 0 ? 0 : _talents.Max(x => x.Row) + 1;

                _talentsAdapter.Execute(
                    $"INSERT INTO `aa_talents` (SpellRank1, SpellRank2, SpellRank3, SpellRank4, SpellRank5, Talent_Type, Comment, Class) " +
                    $"VALUES (0, 0, 0, 0, 0, 'rank', 'New Talent', '{_talentsAdapter.EscapeString(SelectedClass)}');");
                var idTalent = Convert.ToInt32(_talentsAdapter.QuerySingleValue("SELECT LAST_INSERT_ID();"));

                var reqSpellCols = string.Join(", ", Enumerable.Range(1, 9).Select(i => $"Requiredspell{i}"));
                var reqSpellVals = string.Join(", ", Enumerable.Repeat("0", 9));
                var arrowCols = string.Join(", ", Enumerable.Range(1, 9).Select(i => $"Arrow{i}"));
                var arrowVals = string.Join(", ", Enumerable.Repeat("'0'", 9));

                _talentsAdapter.Execute(
                    $"INSERT INTO `aa_talent_slot` " +
                    $"(IDSpec, IDTalent, Comment, Specialization_Type, `row`, `column`, {reqSpellCols}, {arrowCols}, " +
                    "MinumumRequiredTalents, MinimumRequiredLevel, MinimumSpecificRequired) VALUES (" +
                    $"{idSpec}, {idTalent}, 'New Talent', 'class', {nextRow}, 0, {reqSpellVals}, {arrowVals}, 0, 10, 0);");

                LoadTalentsForSpec();
            }
            catch (Exception ex)
            {
                ShowError("Failed to create talent", ex);
            }
        }

        private void DeleteTalentButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedTalent == null || _selectedSpec == null) return;
            var label = string.IsNullOrEmpty(_selectedTalent.SlotComment) ? "SpellRank1 " + _selectedTalent.SpellRank1 : _selectedTalent.SlotComment;
            if (MessageBox.Show(this, $"Delete this talent ({label})?", "Confirm delete",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            try
            {
                _talentsAdapter.Execute($"DELETE FROM `aa_talent_slot` WHERE ID = {_selectedTalent.SlotID};");
                _talentsAdapter.Execute($"DELETE FROM `aa_talents` WHERE ID = {_selectedTalent.IDTalent};");
                LoadTalentsForSpec();
            }
            catch (Exception ex)
            {
                ShowError("Failed to delete talent", ex);
            }
        }

        private void SaveAllButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedSpec != null) SaveSpec(_selectedSpec);
            if (_selectedTalent != null) SaveTalent(_selectedTalent);
        }

        #endregion

        #region Spell name/icon lookups (main spell DB, via _adapter)

        private string GetSpellName(int spellId)
        {
            if (spellId <= 0) return null;
            if (_spellNameCache.TryGetValue(spellId, out var cached)) return cached;

            string name = $"Unknown ({spellId})";
            try
            {
                if (_adapter != null)
                {
                    var value = _adapter.QuerySingleValue($"SELECT SpellName0 FROM `spell` WHERE ID = {spellId};");
                    if (value != null && value != DBNull.Value && !string.IsNullOrEmpty(value.ToString()))
                        name = value.ToString();
                }
            }
            catch
            {
                // leave the "Unknown (id)" fallback - the main spell DB might not be configured/reachable
            }

            _spellNameCache[spellId] = name;
            return name;
        }

        private ImageSource GetSpellIcon(int spellId)
        {
            if (spellId <= 0) return null;
            if (_spellIconCache.TryGetValue(spellId, out var cached)) return cached;

            ImageSource image = null;
            try
            {
                if (_adapter != null)
                {
                    var iconIdObj = _adapter.QuerySingleValue($"SELECT SpellIconID FROM `spell` WHERE ID = {spellId};");
                    if (iconIdObj != null && iconIdObj != DBNull.Value)
                    {
                        var iconId = Convert.ToUInt32(iconIdObj);
                        var loadIcons = DBCManager.GetInstance().FindDbcForBinding("SpellIcon") as SpellIconDBC;
                        if (loadIcons != null)
                        {
                            var filePath = loadIcons.GetIconPath(iconId) + ".blp";
                            image = BlpManager.GetInstance().GetImageSourceFromBlpPath(filePath);
                        }
                    }
                }
            }
            catch
            {
                // leave image null - icon lookup depends on the main spell DB + DBC folder being configured
            }

            _spellIconCache[spellId] = image;
            return image;
        }

        #endregion

        #region Small form-building helpers

        private void ShowError(string context, Exception ex)
        {
            MessageBox.Show(this, context + ":\n" + ex.Message, "Talent Tree Editor",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private static void AddField(Panel parent, string label, Func<string> getter, Action<string> setter, bool multiline = false)
        {
            parent.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 2), FontSize = 11, Foreground = Brushes.Silver });
            var box = new TextBox
            {
                Text = getter() ?? "",
                AcceptsReturn = multiline,
                TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                Height = multiline ? 60 : double.NaN
            };
            box.LostFocus += (s, e) => setter(box.Text);
            parent.Children.Add(box);
        }

        private static int ParseInt(string value, int fallback) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : fallback;

        private static float ParseFloat(string value, float fallback) =>
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : fallback;

        #endregion
    }

    /// <summary>Row from aaa_custom_spell_tab - the spec CARD metadata table (name/icon/background/
    /// description/etc shown on the specialization-picker cards), separate from aa_specialization.</summary>
    public class SpecRow
    {
        public string Class;
        public int Id;
        public string Name;
        public int TP;
        public int MaxActiveSpells;
        public string Icon;
        public string Background;
        public int SpecSpell;
        public string Description;
        public int Spell1, Spell2, Spell3, Spell4, Spell5, Spell6;
        public int Role1, Role2;
        public string Stat;
        public float BackgroundContentRight = 0.79f;
        public float BackgroundContentBottom = 0.76f;
        public float BackgroundVisibleWidth = 0.25f;

        public static SpecRow FromDataRow(DataRow row) => new SpecRow
        {
            Class = row["Class"].ToString(),
            Id = Convert.ToInt32(row["Id"]),
            Name = row["Name"].ToString(),
            TP = Convert.ToInt32(row["TP"]),
            MaxActiveSpells = Convert.ToInt32(row["MaxActiveSpells"]),
            Icon = row["Icon"].ToString(),
            Background = row["Background"].ToString(),
            SpecSpell = Convert.ToInt32(row["SpecSpell"]),
            Description = row["Description"].ToString(),
            Spell1 = Convert.ToInt32(row["Spell1"]),
            Spell2 = Convert.ToInt32(row["Spell2"]),
            Spell3 = Convert.ToInt32(row["Spell3"]),
            Spell4 = Convert.ToInt32(row["Spell4"]),
            Spell5 = Convert.ToInt32(row["Spell5"]),
            Spell6 = Convert.ToInt32(row["Spell6"]),
            Role1 = Convert.ToInt32(row["Role1"]),
            Role2 = Convert.ToInt32(row["Role2"]),
            Stat = row["Stat"].ToString(),
            BackgroundContentRight = Convert.ToSingle(row["BackgroundContentRight"]),
            BackgroundContentBottom = Convert.ToSingle(row["BackgroundContentBottom"]),
            BackgroundVisibleWidth = Convert.ToSingle(row["BackgroundVisibleWidth"]),
        };
    }

    /// <summary>Row read from the aa_vista_talent_slots view (aa_talent_slot join aa_specialization
    /// join aa_talents). SlotID/IDTalent are kept so Save/Delete can write to aa_talent_slot /
    /// aa_talents directly, since the view itself isn't writable.</summary>
    public class TalentSlotRow
    {
        public int SlotID;   // aa_talent_slot.ID
        public int IDSpec;   // aa_talent_slot.IDSpec -> aa_specialization.ID
        public int IDTalent; // aa_talent_slot.IDTalent -> aa_talents.ID

        public string Class;
        public int Specialization; // aa_specialization.Specialization (per-class spec number)

        public int SpellRank1, SpellRank2, SpellRank3, SpellRank4, SpellRank5;
        public string TalentType;         // aa_talents.Talent_Type
        public string SpecializationType; // aa_talent_slot.Specialization_Type
        public int Row;
        public int Column;
        public int[] RequiredSpell = new int[9];
        public string[] Arrow = new string[9];
        public int MinReqTal, MinReqLvl, MinReqSpe;
        public string SlotComment;   // aa_talent_slot.Comment
        public string TalentComment; // aa_talents.Comment

        public IEnumerable<int> SpellIds() => new[] { SpellRank1, SpellRank2, SpellRank3, SpellRank4, SpellRank5 }.Where(x => x > 0);
        public IEnumerable<int> RequiredSpellIds() => RequiredSpell.Where(x => x > 0);

        public static TalentSlotRow FromDataRow(DataRow row)
        {
            var t = new TalentSlotRow
            {
                SlotID = Convert.ToInt32(row["SlotID"]),
                IDSpec = Convert.ToInt32(row["IDSpec"]),
                IDTalent = Convert.ToInt32(row["IDTalent"]),
                Class = row["Class"].ToString(),
                Specialization = Convert.ToInt32(row["Specialization"]),
                SpellRank1 = Convert.ToInt32(row["SpellRank1"]),
                SpellRank2 = Convert.ToInt32(row["SpellRank2"]),
                SpellRank3 = Convert.ToInt32(row["SpellRank3"]),
                SpellRank4 = Convert.ToInt32(row["SpellRank4"]),
                SpellRank5 = Convert.ToInt32(row["SpellRank5"]),
                TalentType = row["Talent_Type"].ToString(),
                SpecializationType = row["Specialization_Type"].ToString(),
                Row = Convert.ToInt32(row["row"]),
                Column = Convert.ToInt32(row["column"]),
                MinReqTal = Convert.ToInt32(row["MinumumRequiredTalents"]),
                MinReqLvl = Convert.ToInt32(row["MinimumRequiredLevel"]),
                MinReqSpe = Convert.ToInt32(row["MinimumSpecificRequired"]),
                SlotComment = row["Slot_Comment"] == DBNull.Value ? "" : row["Slot_Comment"].ToString(),
                TalentComment = row["Talent_Comment"] == DBNull.Value ? "" : row["Talent_Comment"].ToString(),
            };
            for (int i = 0; i < 9; i++)
                t.RequiredSpell[i] = Convert.ToInt32(row[$"Requiredspell{i + 1}"]);
            for (int i = 0; i < 9; i++)
                t.Arrow[i] = row[$"Arrow{i + 1}"].ToString();
            return t;
        }
    }
}
