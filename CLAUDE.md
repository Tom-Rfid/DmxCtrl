# Contrôleur DMX — boîtier Bluetooth RY-AI

Logiciel Windows (C# / .NET 10 / WinForms) pour piloter en DMX, via un boîtier
Bluetooth bon marché « USB-RDM-DMX » (appli Android RY-AI), 2 lyres Chauvet
Intimidator Spot 260X (adresses 1 et 15, mode 14 canaux) et un Mindi Mini Spider
8x3W RGBW (adresse 29, 15 canaux). Protocole obtenu par rétro-ingénierie de l'APK :
voir `PROTOCOLE.md`.

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
- `App/UI/` : `MainForm`, `LivePanel` (Soirée), `FixturePanel`, `DebugPanel`, `PatchDialog`, `ProfileEditorDialog`. IHM construite par code (`[DesignerCategory("Code")]`).
- `Tests/` : xUnit (protocole, moteur, config).
- `tools/` : `cipher.py`, `dmx_test.py` (test v1), `gen_reference_frame.py`.
- `archive/v1-test-clignotement/` : version qui a validé le protocole.

Convention « univers » : tableau de 513 octets, index N = canal DMX N (index 0 inutilisé).
Commentaires et textes d'IHM en français.

## À faire

1. Tester la v2 avec le boîtier (connexion, onglets, scènes, reconnexion auto).
2. Vérifier les captures d'écran de l'IHM (mise en page des groupes de l'onglet Soirée).
3. **Profil 260X : ordre des 14 canaux SUPPOSÉ** → vérifier canal par canal (onglet Debug / cases « OK »), corriger dans l'éditeur de profils.
4. Relever les presets de la roue de couleurs 260X (bouton « + ») et la valeur « obturateur ouvert ».
5. Spider : confirmer la couleur des LED 5-12 (supposées R,V,B,Blanc ×2).
6. Tester une cadence d'envoi < 100 ms (onglet Debug) pour des mouvements plus fluides.
7. Mettre à jour le doc Claude Docs « Protocole DMX RY-AI Bluetooth » (statut + nouvelle archi).
8. Plus tard : scan RDM (voir PROTOCOLE.md §7), portage Mac (Core est prêt), contrôleur MIDI.

## Règles de sécurité

- Ne jamais écrire automatiquement sur les canaux de type `Control` (reset / modes auto).
- Le mode debug remplace toute la sortie : il est signalé en rouge dans la barre d'état.
