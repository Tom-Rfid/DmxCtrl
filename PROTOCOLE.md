# Protocole DMX-over-Bluetooth du boitier RY-AI — historique du reverse engineering

> Document de reference pour reprendre ce projet (y compris avec Claude Code).
> Tout ce qui est decrit ici a ete verifie — soit par des checks automatiques
> (permutations, round-trip chiffrement), soit par compilation reelle du projet
> C#. Les points encore incertains sont marques **[A VERIFIER]**.

## 1. Objectif du projet

Piloter en DMX, depuis un PC Windows, via un boitier Bluetooth bon marche
("USB-RDM-DMX", vendu avec l'appli Android **RY-AI**), le materiel suivant :

| Appareil | Adresse DMX | Canaux |
|---|---|---|
| Chauvet DJ Intimidator Spot 260X #1 | 1 | 1 – 14 (mode 14CH) |
| Chauvet DJ Intimidator Spot 260X #2 | 15 | 15 – 28 (mode 14CH) |
| Mindi Mini Spider 8x3W RGBW | 29 | 29 – 43 (15 canaux) |

**[A VERIFIER]** : l'adressage ci-dessus a ete choisi en suivant la doc Chauvet
generale, mais la fonction precise de chaque canal (lequel est Pan, Tilt,
dimmer, couleur...) pour la 260X n'a pas ete confirmee avec le manuel officiel
ni par test physique systematique. L'IHM C# fournie expose donc des sliders
generiques "Canal N" plutot que des noms de fonction.

## 2. Le boitier et sa liaison

- Nom Bluetooth observe : `RY-A1096A-6319`
- Chipset probable : Jieli (puce bon marche tres utilisee pour le Bluetooth-serie,
  indice : le port Windows s'appelle `JL_SPP`)
- **Protocole radio : Bluetooth classique, profil SPP standard**
  (UUID `00001101-0000-1000-8000-00805F9B34FB`), PAS du BLE.
  Confirme dans le code de l'appli : `BtClient.java`,
  `bluetoothDevice.createRfcommSocketToServiceRecord(SPP_UUID)`.
- Consequence pratique : Windows cree automatiquement 2 ports COM virtuels a
  l'appairage (chez nous `COM5` entrant, `COM6` sortant). **C'est `COM6`
  (sortant) qu'il faut utiliser** : c'est le role "client" que joue le
  telephone/PC quand il se connecte au boitier (l'appli Android fait pareil
  via `createRfcommSocketToServiceRecord`).
- Le debit (baud rate) choisi pour ouvrir le port (`9600` dans notre code)
  n'a pas d'importance reelle : le port COM est virtuel, cree par le pilote
  Bluetooth, pas par un vrai UART.

## 3. Poignees de main (handshake)

Trouvees dans `ConsoleFragment.java` (`SendBTConnectData()` /
`SendBTDisConnectData()`). Octets fixes, envoyes en clair :

```
Connexion    : BB 99 77 01 77 99
Deconnexion  : BB 77 99 10 99 77 00 55
```

La connexion s'envoie juste apres l'ouverture du port ; la deconnexion juste
avant de le fermer.

## 4. Structure d'une trame DMX (521 octets)

Reconstituee depuis `ConsoleFragment.SendConsloeData()` (boucle d'envoi a
~10 Hz, soit une trame toutes les 100 ms, tout le temps que le "mode console"
est actif — exactement comme un vrai controleur DMX filaire qui rafraichit en
continu) :

```
[0]        0x00
[1]        'K' (0x4B)      }  marqueurs de debut, en clair
[2]        'T' (0x54)      }
[3]        octet "scene"   (0 = pilotage manuel direct)
[4]        octet "vitesse" (0 en mode manuel)
[5..516]   512 octets = univers DMX complet (canal 1 -> octet 5, canal 512 -> octet 516)
           APRES brouillage (voir section 5)
[517]      octet de cle n[1], ecrit par l'algorithme de brouillage lui-meme
[518]      octet de cle n[2] (= le compteur "codeNum" utilise pour CETTE trame)
[519]      'T' (0x54)      }  marqueurs de fin, en clair
[520]      'K' (0x4B)      }
```

Le mapping canal -> octet : `octet = 4 + numero_de_canal` (canal 1..512).

Une subtilite trouvee en tracant le code Java : le buffer de scene (97 octets,
utilise pour les "scenes" memorisees 1-8) a son octet d'indice 0 toujours mis a
zero juste apres avoir ete copie dans la trame — en pratique, pour du pilotage
manuel, seuls les octets 1 a 96 de ce buffer comptent, et ils tombent
exactement sur les canaux DMX 1 a 96.

## 5. Le brouillage ("Code.java" / cipher)

Ce n'est **pas** un vrai chiffrement cryptographique, juste une substitution
par table (type S-box), mais il FAUT le reproduire exactement, sinon le
boitier ignore la trame.

- 5 tables de substitution `password[0..4][0..255]` (encodage) et 5 tables
  inverses `decode[0..4][0..255]` (decodage), chacune une permutation
  complete des 256 valeurs d'un octet.
- 1 table `sin[0..255]` qui ressemble a une onde sinusoidale (valeurs 0-255),
  utilisee pour :
  1. choisir la table a utiliser : `password[sin[codeNum] % 5]`
  2. fournir l'octet de cle ecrit en fin de trame : `sin[codeNum]` lui-meme
- Algorithme d'encodage (`Encryption1` en Java, `Cipher.Encrypt` en C#,
  `encryption1` en Python) :
  ```
  table = password[ sin[codeNum] % 5 ]
  pour chaque octet "v" en clair a la position p :
      octet_brouille[p] = table[ (v + codeNum) % 256 ]
  en fin de trame, a markerOffset :
      trame[markerOffset]     = sin[codeNum]
      trame[markerOffset + 1] = codeNum
  ```
- `codeNum` va de 0 a 255 en boucle ; le boitier n'a pas besoin d'etre
  synchronise avec un compteur cote appli, puisque la valeur de `codeNum`
  utilisee pour CETTE trame est elle-meme ecrite DANS la trame (octet 518).

### Note sur les constantes "MASK / DRAG / SWIPE"

En decompilant l'APK avec jadx, 3 nombres litteraux de `Code.java`
apparaissent remplaces par des noms de constantes Android/Kotlin qui n'ont
RIEN A VOIR avec notre sujet (`kotlinx.coroutines.scheduling.WorkQueueKt.MASK`,
`androidx.recyclerview.widget.ItemTouchHelper.Callback.DEFAULT_DRAG_ANIMATION_DURATION`,
`...DEFAULT_SWIPE_ANIMATION_DURATION`). C'est un artefact cosmetique de jadx :
il remplace un litteral par le nom d'une constante visible dans le classpath
qui a, par coincidence, exactement la meme valeur numerique. On a determine
par elimination (puis **confirme par verification automatique**, voir
section 6) que :

```
MASK  = 127
DRAG  = 200
SWIPE = 250
```

## 6. Validation effectuee

Un script Python (`cipher.py`, dans ce depot) :
- reconstruit les 10 tables (5 password + 5 decode) et la table sin, avec
  `MASK=127`, `DRAG=200`, `SWIPE=250` ;
- verifie que chaque table `password[i]` et `decode[i]` est bien une
  permutation complete de 0..255 (longueur 256, sommes = 32640, 256 valeurs
  distinctes) — **toutes OK** ;
- fait un test aller-retour chiffrement -> dechiffrement sur un buffer
  aleatoire, pour plusieurs valeurs de `codeNum` — **tous OK**.

Le port C# (`Cipher.cs`, dans le projet Visual Studio) reprend exactement les
memes tables et a ete verifie en compilant le projet entier avec succes
(`dotnet build`, 0 erreur, 0 avertissement).

**Non encore teste physiquement** : l'envoi reel d'une trame sur le boitier
(script `dmx_test.py` fourni, ou directement l'appli C#) pour confirmer que
les lyres / le Spider reagissent bien.

## 7. Autres elements trouves mais pas (encore) utilises

- **Protocole RDM** (`RDMFragment.java`, `MyDeviceAdapter.java`, methode
  `extractRDMData` dans `util.java`) : scan des appareils RDM sur le bus,
  lecture/ecriture d'adresse DMX et de mode de canaux a distance. Trames de
  46 octets en reponse, avec checksum (`CheckRDMSum`). Pas implemente dans
  l'IHM C# actuelle (qui suppose un adressage DMX fixe deja configure a la
  main sur les appareils), mais toute la logique de parsing a ete tracee et
  documentee si on veut l'ajouter plus tard.
- **"Download" vers la memoire du boitier** (`SendDownData()`) : marqueurs
  `44 55` (`"DU"`)/`55 44` (`"UD"`) a la place de `4B 54`/`54 4B`
  (`"KT"`/`"TK"`). Semble permettre de memoriser des scenes directement dans
  le boitier pour un fonctionnement autonome (sans telephone/PC). Pas
  implemente.
- **Decodage des trames recues** (`util.transaleBTData`, `DisposeConsoleData`) :
  utile si on veut un jour lire les reponses du boitier. Le port C# inclut
  deja `Cipher.DecodeFrame()` (inverse de `Encrypt`) pour cet usage futur.

## 8. Fichiers du projet

```
DmxController.sln          solution Visual Studio
App/
  DmxController.csproj     projet .NET (net10.0-windows, WinForms,
                           package NuGet System.IO.Ports)
  Program.cs               point d'entree
  Form1.cs                 interface graphique (sliders par canal, connexion)
  Form1.Designer.cs        config de base de la fenetre
  Cipher.cs                le brouillage reconstitue (voir section 5)
  DmxLink.cs                gestion du port serie + boucle d'envoi 10 Hz
```

Fichiers Python de la phase d'analyse (hors projet C#, gardes comme preuve/
reference) : `cipher.py` (validation des tables), `dmx_test.py` (script de
test en ligne de commande, equivalent du programme C# en plus simple).

## 9. Pour continuer

1. Ouvrir `DmxController.sln` dans Visual Studio 2022 (Insiders ou stable),
   verifier que le port `COM6` (sortant) est bien liste, Connecter, puis
   essayer le bouton "Demarrer le test clignotement".
2. Si ca clignote : le protocole est 100% confirme. Reste a identifier le
   VRAI role de chaque canal 1-14 (Pan ? Tilt ? Couleur ? Dimmer ?) sur la
   260X, en bougeant un slider a la fois et en observant la lyre, ou en
   retrouvant le manuel DMX officiel Chauvet pour le mode 14CH. Une fois
   confirme, renommer les labels dans `Form1.BuildChannelGroup()`.
3. Ameliorations possibles ensuite : effets automatiques (balayage, cercle,
   synchronisation), sauvegarde de scenes (fichier JSON local plutot que la
   memoire du boitier), implementation du scan RDM (section 7) pour detecter
   automatiquement les adresses au lieu de les supposer fixes.
