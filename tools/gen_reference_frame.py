"""
Genere une trame de reference avec le chiffrement Python (cipher.py, valide sur
le vrai materiel) pour que les tests C# verifient qu'ils produisent EXACTEMENT
les memes octets. Entree : canal 1=255, 2=128, 3=1, 14=42, 512=7, codeNum=1.
"""
from cipher import encryption1

universe = [0] * 513
universe[1], universe[2], universe[3], universe[14], universe[512] = 255, 128, 1, 42, 7

buf = bytearray(521)
buf[1], buf[2] = ord('K'), ord('T')
buf[5:517] = bytes(universe[1:513])
buf[519], buf[520] = ord('T'), ord('K')

print(encryption1(buf, 5, 512, 1, 517).hex())
