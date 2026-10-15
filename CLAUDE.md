# Contrôleur DMX — boîtier Bluetooth RY-AI

Logiciel Windows (C# / .NET 10 / WinForms) pour piloter en DMX, via un boîtier
Bluetooth bon marché « USB-RDM-DMX » (appli Android RY-AI), 2 lyres Chauvet
Intimidator Spot 260X (adresses 16 et 30, mode 14 canaux) et un Mindi Mini Spider
8x3W RGBW (adresse 1 : canaux 1-15). Protocole obtenu par rétro-ingénierie de l'APK :
voir `PROTOCOLE.md`.

## Statut (2026-10-16, suite)

- Config de référence de l'utilisateur : `%APPDATA%\DmxController\Configurations\Soirée par défaut.dmxshow_3.dmxshow.json`
  (lyres en 1 et 15, Spider en 29, ailes en miroir) ; `DefaultShow` reprend ce patch.
- Spider : effet gauche ↔ droite (onglet Spider) avec délai, amplitude, ou calé sur le tempo (N temps par aller) ;
  suit la case « Inverser ». Moteur : `SetWingSweep`, recalage sans saut quand le BPM change.
- Micro (onglet Soirée, groupe « Micro ») : `Core/Audio/BeatDetector` (autocorrélation des attaques → BPM 80-160,
  niveau en dB, dimmer qui baisse quand le volume chute puis remonte en N s) + `App/Audio/MicInput` (NAudio.WinMM).
  Cases « Caler le BPM sur le micro » et « Dimmer suit le volume ». Testé sur signaux synthétiques seulement :
  **à régler en vrai** (DeadZoneDb 8, DimmerRangeDb 18, lissage 0,5 s).

## Statut (2026-10-16)

- **Bibliothèque de modèles** (`Core/Model/FixtureLibrary.cs`) : 260X 14 et 8 canaux (table officielle
  Chauvet, guide rapide Rev. 1 — l'ancienne table « supposée » était fausse à partir du canal 10),
  Mini Spider, fumée Ayra WSM02 MKIII (1 canal), laser Showtec Galactic RGY-140 MKII (10 canaux, manuel
  Highlite 51335), PAR LED 36 RGB+UV **générique (canaux à vérifier, marque inconnue)**.
  `UpgradeProfiles()` corrige les profils des anciennes configs (champ `Revision`), valeurs des scènes
  reclassées par rôle.
- Patch refait : liste + fiche ; choisir un modèle recharge le nombre de canaux et propose une adresse
  libre (garde l'actuelle si elle tient, sinon 1re plage libre). Appareils désactivés : aucune alerte,
  onglet grisé avec bouton « Activer ». Case « Flash / strobe » → interrupteurs FLASH/STROBE dans Soirée
  (strobe matériel via `ChannelDef.StrobeSlow/StrobeFast`, sinon logiciel).
- Fumée : bandeau en haut (état estimé PRÊTE/CHAUFFE, FUMÉE/PAS DE FUMÉE, manuel/coup/auto) + onglet
  réglages (débit, durée, délai, chauffe). Le DMX ne renvoie RIEN : ni état prêt ni température.
- Laser : onglet spécial (allumer/éteindre, mode, 32 motifs, couleurs, pad X/Y, plages de la notice) ;
  blackout ou master 0 → laser en mode DMX + couleur 0 (éteint).
- Bug corrigé : groupes de l'onglet Soirée écrasés (contenu ancré dans un GroupBox AutoSize, `Ui.Group`).
- 43 tests OK, 0 avertissement. Captures vérifiées via un harnais hors dépôt. Pas encore testé avec le boîtier.

## Statut (2026-10-15)

- Protocole **confirmé sur le vrai matériel** (test de clignotement OK, COM6 sortant).
- Trame C# identique octet pour octet au Python validé (test `ProtocolTests`).
- v2 : configs JSON, onglet Soirée, un onglet par appareil, Debug, éditeurs de patch
  et de profils. Compile sans avertissement, 24 tests OK, lancement + fermeture OK.
  **Pas encore testée en vrai avec le boîtier** (seule la v1 l'a été).

## Commandes

```
dotnet build DmxController.slnx
dotnet test Tests/DmxController.Tests.csproj
dotnet run --project App
```

## Architecture

- `Core/` (net10.0, sans WinForms → réutilisable pour un portage Mac/Linux)
  - `Protocol/` : `Cipher` (brouillage), `DmxFrame` (trame 521 octets), `DmxLink` (port série + thread d'envoi 10 Hz)
  - `Model/` : profils, patch (`FixtureInstance`), scènes, couleurs, `PatchValidator`, `DefaultShow`
  - `Engine/ShowEngine` : calcule l'univers à chaque trame (programmeur → fondu → mouvement → chenillard → strobe/master/blackout/flash). Thread-safe via `_lock` ; toute modif de structure passe par `EditShow()`.
  - `Storage/ShowStore` : fichiers `*.dmxshow.json`, `*.dmxprofile.json`, `%APPDATA%\DmxController\settings.json`
- `App/UI/` : `MainForm`, `LivePanel` (Soirée), `FixturePanel` (onglet générique), `FogPanel` (`FogStrip`, `FogBanner`), `LaserPanel` (`RangeControl`), `DebugPanel`, `PatchDialog`, `ProfileEditorDialog`. IHM construite par code (`[DesignerCategory("Code")]`). Onglet choisi d'après le profil : `IsFogMachine` → fumée, `IsLaser` → laser, sinon générique.
  - Piège WinForms : jamais de `Dock` sur le contenu d'un GroupBox `AutoSize` (ni de FlowLayoutPanel à sauts de ligne autour de GroupBox AutoSize) → mise en page écrasée.
  - `FixturePanel` : pad pan/tilt pour les lyres ; pour un appareil à 2 moteurs (Spider) : pad ailes (X = position, Y = dimmer), case « Inverser » (ailes en miroir, `FixtureInstance.MirrorMotors`), faders verticaux LED gauche/droite (`LedGroups`, LED 1-4 / 5-8 supposées), case « Pas de strobe » (`FixtureInstance.NoStrobe`, forcé à 0 au rendu).
- `Tests/` : xUnit (protocole, moteur, config).
- `tools/` : `cipher.py`, `dmx_test.py` (test v1), `gen_reference_frame.py`.
- `archive/v1-test-clignotement/` : version qui a validé le protocole.

Convention « univers » : tableau de 513 octets, index N = canal DMX N (index 0 inutilisé).
Commentaires et textes d'IHM en français.

## À faire

1. Tester la v2 avec le boîtier (connexion, onglets, scènes, reconnexion auto).
2. Vérifier les captures d'écran de l'IHM (mise en page des groupes de l'onglet Soirée).
3. Profil 260X : table officielle Chauvet en place (presets couleurs/gobos/obturateur) → confirmer sur la lyre.
4. PAR LED 36 RGB+UV : demander la marque / le menu DMX à l'utilisateur et corriger le profil générique.
4b. Fumée : confirmer le débit (0-255 = 0-100 %) et le temps de chauffe ; laser : confirmer que le canal 1 en 240-255 = DMX.
5. Spider VALIDÉ (2026-10-16) : moteur 1 = aile gauche, moteur 2 = aile droite ; LED 1-4 gauche, LED 5-8 droite (rouge, vert, bleu, blanc). Le décalage d'un canal vu au début venait probablement d'un seul appareil branché en DMX.
   Le décalage d'un canal observé au début a disparu (2026-10-16) : profil revenu à 15 canaux (révision 2,
   les anciennes configs à 16 canaux sont converties automatiquement).
6. Tester une cadence d'envoi < 100 ms (onglet Debug) pour des mouvements plus fluides.
7. Mettre à jour le doc Claude Docs « Protocole DMX RY-AI Bluetooth » (statut + nouvelle archi).
8. Plus tard : scan RDM (voir PROTOCOLE.md §7), portage Mac (Core est prêt), contrôleur MIDI.

## Règles de sécurité

- Ne jamais écrire automatiquement sur les canaux de type `Control` (reset / modes auto).
- Le mode debug remplace toute la sortie : il est signalé en rouge dans la barre d'état.
