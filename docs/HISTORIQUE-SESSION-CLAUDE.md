# DMX Controller IHM — résumé de la session Claude (octobre 2026)

Résumé à garder dans le projet claude.ai « Dmx Controller IHM » pour reprendre le travail avec le contexte.
Code : https://github.com/Tom-Rfid/DmxCtrl (dossier local `C:\Users\tomme\Documents\DmxController`).

## Matériel
- Boîtier Bluetooth « USB-RDM-DMX » (appli Android RY-AI), port COM « sortant » (COM6 chez moi).
  Trames de 521 octets (KT…TK), brouillées par tables, envoyées toutes les 100 ms.
- 2 × Chauvet Intimidator Spot 260X (14 canaux) : adresses 1 et 15.
- Mini Spider 8x3W RGBW (15 canaux, validé) : adresse 29.
- PAR LED 36 UV U'King ZQ01087 (7 canaux) : adresse 44 (menu d044).
- Laser Showtec Galactic RGY-140 MKII, version rouge/vert seulement (10 canaux) : adresse 51.
  ⚠ Le programme « Son rouge » (valeur 195 sur le canal 1) met le laser en mode test « tst » : il n'écoute plus le DMX.
  Sortie sur l'appareil : FUNC → adresse DMX → ENTER, ou l'éteindre et le rallumer.
  Zoom : en dessous de 60 le motif se déforme (60 par défaut).
- Machine à fumée Ayra WSM02 MKIII (1 canal) : adresse 61 (DIP 1+4+8+16+32). Elle ne renvoie pas son état « prête ».
- Une machine « T-400W » sans DMX : non ajoutée (piste : relais DMX sur le bouton de la télécommande).

## Fonctions de l'appli (WinForms .NET 10, C#)
- Onglet **Soirée** : couleurs, mouvements, chenillard, strobe, master, blackout, flash, tap tempo, micro (tempo et dimmer qui suit le volume), scènes et mode auto.
- Onglet **Balayage** : lyres (+ Spider) gauche/droite ensemble, au pad ou en automatique (délai, BPM ou tempo du micro).
  Mode croisé selon la place sur scène (gauche/milieu/droite), Spider en miroir, dimmers commun/lyres/Spider.
- Un onglet par appareil, généré depuis son profil :
  - Spider : ailes, masque de couleurs, effet gauche/droite.
  - PAR : groupes A/B/C, TOUT COUPER, effet au tempo.
  - Laser : 32 motifs dessinés, couleurs par type (fixe, alternance, défilement), zoom 60, inversion X/Y.
  - Fumée : maintenir, coup de 0,5 à 5 s, mode auto un jet toutes les 1 à 30 min, chauffe estimée, commandes « toutes les machines ».
- **Patch** : adresses libres proposées, appareils désactivables, place sur scène.
- **Éditeur de profils** et onglet **Debug** (curseurs bruts, moniteur, cadence d'envoi).
- Option d'envoi « seulement si changement + trame de maintien ».
- **Langue** : Paramètres > Langue / Language. Anglais par défaut, français pour les installations existantes.
  Les textes français du code servent de clés ; la table anglaise est dans `Core/Localization/Tr.En.cs`.
- Reconnexion Bluetooth automatique toutes les 3 s si la liaison est perdue.
- Commentaires du code en anglais. Tests : 68 (xUnit).

## Exe portable
- `publish.cmd` génère `publish\win-x64\DmxController.exe` : un seul fichier de 52 Mo, rien à installer.
- Il ajoute à côté le `README.txt` et le dossier `Examples` (configurations d'exemple, à ouvrir par Fichier > Ouvrir / importer).
- Les configurations de l'utilisateur sont dans `%APPDATA%\DmxController\Configurations`.

## Tags Git
spider-ok, spider-ok-v2, par-uv-ok, laser-ok, fog-ok, english-ok.

## Pistes non faites
- Mettre à jour la doc du dépôt JL_SPP-ProtocolLib-DMX (ancien décalage d'un canal sur le Spider).
- Faire réagir le laser au tempo du micro.
- Ajouter une machine à fumée pilotée par un relais DMX.
