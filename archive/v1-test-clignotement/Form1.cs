// =====================================================================================
//  Form1.cs
// -------------------------------------------------------------------------------------
//  Fenetre principale de l'application. Contient :
//    - en haut : la selection du port COM Bluetooth et les boutons Connecter/Deconnecter
//    - au milieu : 3 onglets (Lyre 1 / Lyre 2 / Spider), chacun avec un slider par
//      canal DMX utilise (voir DmxLink.cs pour le detail de l'adressage)
//    - en bas : un bouton "Blackout" (tout remettre a 0) et un bouton de test qui
//      fait clignoter tous les canaux pour verifier rapidement que la liaison marche
//
//  IMPORTANT : les sliders sont nommes "Canal N" (generiques) et PAS "Pan", "Tilt",
//  "Couleur", etc. En effet, si on a bien confirme l'ADRESSAGE DMX (quelle lyre
//  commence a quel canal), on n'a pas encore verifie dans le manuel officiel Chauvet
//  quel canal precis correspond a quelle fonction pour la 260X. Utilise les sliders
//  un par un (en observant la lyre) pour completer ce mapping, puis renomme les
//  labels dans BuildChannelGroup() une fois que tu es sur de toi. Voir PROTOCOLE.md.
// =====================================================================================

namespace DmxController;

public partial class Form1 : Form
{
    // Objet qui gere la connexion au boitier Bluetooth et l'envoi continu des trames
    // DMX (voir DmxLink.cs). Toute la logique "protocole" est la-bas ; cette fenetre
    // ne fait que refleter / modifier l'etat de ses 512 canaux.
    private readonly DmxLink _dmxLink = new();

    // Un slider (TrackBar) + un label d'affichage de la valeur, par canal DMX.
    // Cle = numero de canal DMX (1 a 512, mais on n'en utilise que 1 a 43 ici).
    private readonly Dictionary<int, TrackBar> _sliders = new();
    private readonly Dictionary<int, Label> _valueLabels = new();

    // Controles du bandeau superieur (connexion).
    private ComboBox _portCombo = null!;
    private Button _btnConnect = null!;
    private Button _btnDisconnect = null!;
    private Label _lblStatus = null!;

    // Minuteur (cote interface, PAS le thread d'envoi DMX) utilise uniquement pour
    // le bouton "Test clignotement" : il change la valeur des canaux chaque seconde,
    // la boucle d'envoi de DmxLink se charge ensuite de transmettre ces nouvelles
    // valeurs au rythme normal de 10 Hz.
    private System.Windows.Forms.Timer? _blinkTimer;
    private bool _blinkOn;

    public Form1()
    {
        InitializeComponent();
        BuildUserInterface();
    }

    // ---------------------------------------------------------------------------
    //  Construction de l'interface (entierement par code, voir Form1.Designer.cs)
    // ---------------------------------------------------------------------------

    private void BuildUserInterface()
    {
        // --- Bandeau du haut : choix du port COM + connexion -------------------
        var topPanel = new Panel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(8) };

        var lblPort = new Label { Text = "Port COM :", AutoSize = true, Location = new Point(8, 14) };

        _portCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(85, 10),
            Width = 90,
        };
        RefreshPortList();

        var btnRefresh = new Button { Text = "Actualiser", Location = new Point(185, 9), Width = 85 };
        btnRefresh.Click += (_, _) => RefreshPortList();

        _btnConnect = new Button { Text = "Connecter", Location = new Point(280, 9), Width = 90 };
        _btnConnect.Click += OnConnectClicked;

        _btnDisconnect = new Button { Text = "Deconnecter", Location = new Point(375, 9), Width = 95, Enabled = false };
        _btnDisconnect.Click += OnDisconnectClicked;

        _lblStatus = new Label
        {
            Text = "Non connecte",
            ForeColor = Color.DarkRed,
            AutoSize = true,
            Location = new Point(480, 14),
            Font = new Font(Font, FontStyle.Bold),
        };

        topPanel.Controls.AddRange(new Control[] { lblPort, _portCombo, btnRefresh, _btnConnect, _btnDisconnect, _lblStatus });

        // --- Bandeau du bas : blackout + test de clignotement -------------------
        var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(8) };

        var btnBlackout = new Button { Text = "Blackout (tout a 0)", Location = new Point(8, 9), Width = 150 };
        btnBlackout.Click += OnBlackoutClicked;

        var btnBlinkTest = new Button { Text = "Demarrer le test clignotement", Location = new Point(166, 9), Width = 220 };
        btnBlinkTest.Click += (_, _) => ToggleBlinkTest(btnBlinkTest);

        bottomPanel.Controls.AddRange(new Control[] { btnBlackout, btnBlinkTest });

        // --- Milieu : un onglet par appareil, un slider par canal ---------------
        var tabs = new TabControl { Dock = DockStyle.Fill };

        tabs.TabPages.Add(BuildChannelGroup("Lyre 1 - Intimidator 260X (adresse DMX 1)", startChannel: 1, channelCount: 14));
        tabs.TabPages.Add(BuildChannelGroup("Lyre 2 - Intimidator 260X (adresse DMX 15)", startChannel: 15, channelCount: 14));
        tabs.TabPages.Add(BuildChannelGroup("Spider 8x3W RGBW (adresse DMX 29)", startChannel: 29, channelCount: 15));

        // L'ordre d'ajout compte pour le Dock : Top/Bottom d'abord, Fill en dernier.
        Controls.Add(tabs);
        Controls.Add(bottomPanel);
        Controls.Add(topPanel);
    }

    /// <summary>
    /// Construit un onglet contenant une rangee "Canal N / slider 0-255 / valeur"
    /// pour chaque canal DMX de [startChannel, startChannel + channelCount - 1].
    /// </summary>
    private TabPage BuildChannelGroup(string title, int startChannel, int channelCount)
    {
        var page = new TabPage(title);

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            ColumnCount = 3,
            RowCount = channelCount,
            Padding = new Padding(12),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 50));

        for (int offset = 0; offset < channelCount; offset++)
        {
            int channel = startChannel + offset;
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

            var channelLabel = new Label
            {
                Text = $"Canal {channel}",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                TextAlign = ContentAlignment.MiddleLeft,
            };

            var slider = new TrackBar
            {
                Minimum = 0,
                Maximum = 255,
                TickFrequency = 32,
                Dock = DockStyle.Fill,
                Tag = channel, // on retrouve le numero de canal dans le gestionnaire d'evenement
            };
            slider.ValueChanged += OnSliderValueChanged;

            var valueLabel = new Label
            {
                Text = "0",
                AutoSize = true,
                Anchor = AnchorStyles.Right,
                TextAlign = ContentAlignment.MiddleRight,
            };

            table.Controls.Add(channelLabel, 0, offset);
            table.Controls.Add(slider, 1, offset);
            table.Controls.Add(valueLabel, 2, offset);

            _sliders[channel] = slider;
            _valueLabels[channel] = valueLabel;
        }

        page.Controls.Add(table);
        return page;
    }

    // ---------------------------------------------------------------------------
    //  Gestionnaires d'evenements
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Appele quand n'importe quel slider de canal change de position. Met a jour
    /// le label de valeur ET le buffer de canaux dans DmxLink. On n'envoie rien ici
    /// directement sur le port serie : c'est la boucle 10 Hz de DmxLink (demarree
    /// par StartSending()) qui transmettra cette nouvelle valeur a la prochaine
    /// trame, exactement comme le ferait l'appli Android.
    /// </summary>
    private void OnSliderValueChanged(object? sender, EventArgs e)
    {
        var slider = (TrackBar)sender!;
        int channel = (int)slider.Tag!;
        byte value = (byte)slider.Value;

        _valueLabels[channel].Text = value.ToString();
        _dmxLink.SetChannel(channel, value);
    }

    private void OnConnectClicked(object? sender, EventArgs e)
    {
        if (_portCombo.SelectedItem is not string portName)
        {
            MessageBox.Show(this, "Choisis d'abord un port COM dans la liste.", "Port manquant",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            _dmxLink.Connect(portName);
            _dmxLink.StartSending(); // demarre immediatement le rafraichissement DMX a 10 Hz

            _lblStatus.Text = $"Connecte sur {portName}";
            _lblStatus.ForeColor = Color.DarkGreen;
            _btnConnect.Enabled = false;
            _btnDisconnect.Enabled = true;
            _portCombo.Enabled = false;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Impossible de se connecter a {portName} :\n{ex.Message}",
                "Erreur de connexion", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnDisconnectClicked(object? sender, EventArgs e)
    {
        StopBlinkTestIfRunning();
        _dmxLink.Disconnect();

        _lblStatus.Text = "Non connecte";
        _lblStatus.ForeColor = Color.DarkRed;
        _btnConnect.Enabled = true;
        _btnDisconnect.Enabled = false;
        _portCombo.Enabled = true;
    }

    private void OnBlackoutClicked(object? sender, EventArgs e)
    {
        StopBlinkTestIfRunning();
        _dmxLink.Blackout();

        // On remet aussi tous les sliders visuellement a 0 (ce qui redeclenche
        // OnSliderValueChanged pour chacun, sans effet de bord puisque la valeur
        // DMX correspondante est de toute facon deja a 0 via Blackout()).
        foreach (var slider in _sliders.Values)
        {
            slider.Value = 0;
        }
    }

    /// <summary>
    /// Demarre ou arrete le test de clignotement : toutes les secondes, bascule
    /// les canaux 1 a 43 entre 255 et 0. Utile pour verifier que la liaison
    /// fonctionne sans connaitre a l'avance quel canal fait quoi.
    /// </summary>
    private void ToggleBlinkTest(Button button)
    {
        if (_blinkTimer is null)
        {
            if (!_dmxLink.IsConnected)
            {
                MessageBox.Show(this, "Connecte-toi d'abord au port COM.", "Pas connecte",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _blinkOn = false;
            _blinkTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _blinkTimer.Tick += (_, _) =>
            {
                _blinkOn = !_blinkOn;
                byte value = _blinkOn ? (byte)255 : (byte)0;

                for (int channel = 1; channel <= 43; channel++)
                {
                    if (_sliders.TryGetValue(channel, out var slider))
                    {
                        slider.Value = value; // declenche OnSliderValueChanged -> met a jour DmxLink
                    }
                }
            };
            _blinkTimer.Start();
            button.Text = "Arreter le test clignotement";
        }
        else
        {
            StopBlinkTestIfRunning();
            button.Text = "Demarrer le test clignotement";
        }
    }

    private void StopBlinkTestIfRunning()
    {
        _blinkTimer?.Stop();
        _blinkTimer?.Dispose();
        _blinkTimer = null;
    }

    /// <summary>Recharge la liste des ports COM disponibles (ex: COM5, COM6...).</summary>
    private void RefreshPortList()
    {
        string? previouslySelected = _portCombo.SelectedItem as string;

        _portCombo.Items.Clear();
        var ports = System.IO.Ports.SerialPort.GetPortNames().OrderBy(p => p).ToArray();
        _portCombo.Items.AddRange(ports);

        if (previouslySelected is not null && _portCombo.Items.Contains(previouslySelected))
        {
            _portCombo.SelectedItem = previouslySelected;
        }
        else if (_portCombo.Items.Count > 0)
        {
            _portCombo.SelectedIndex = _portCombo.Items.Count - 1;
        }
    }
}
