"""
Test physique du protocole RY-AI / RYBT32 reconstruit depuis l'APK.
Ouvre le port COM Bluetooth SPP (sortant, cote PC = client) et fait
clignoter les canaux 1-43 (= tes 2 Intimidator 260X + le Spider en
adressage 1 / 15 / 29) a pleine valeur puis a zero, pour confirmer
que le protocole marche sans connaitre le mapping exact pan/tilt/couleur.

Usage:
    python dmx_test.py COM6
"""

import sys
import time
import serial

from cipher import encryption1

HANDSHAKE_CONNECT = bytes([0xBB, 0x99, 0x77, 0x01, 0x77, 0x99])
HANDSHAKE_DISCONNECT = bytes([0xBB, 0x77, 0x99, 0x10, 0x99, 0x77, 0x00, 0x55])

FRAME_LEN = 521
DMX_UNIVERSE_SIZE = 512


class DmxLink:
    def __init__(self, port: str, baudrate: int = 9600):
        self.ser = serial.Serial(port, baudrate=baudrate, timeout=1)
        self.channels = bytearray(DMX_UNIVERSE_SIZE + 1)  # index 1..512 utilise
        self.code_num = 0

    def connect(self):
        self.ser.write(HANDSHAKE_CONNECT)
        self.ser.flush()
        time.sleep(0.2)

    def disconnect(self):
        try:
            self.ser.write(HANDSHAKE_DISCONNECT)
            self.ser.flush()
        except Exception:
            pass

    def set_channel(self, channel: int, value: int):
        """channel: 1..512, value: 0..255"""
        self.channels[channel] = value & 0xFF

    def send_frame(self, scene_byte: int = 0, speed_byte: int = 0):
        buf = bytearray(FRAME_LEN)
        buf[0] = 0x00
        buf[1] = ord('K')
        buf[2] = ord('T')
        buf[3] = scene_byte
        buf[4] = speed_byte
        buf[5:5 + DMX_UNIVERSE_SIZE] = self.channels[1:1 + DMX_UNIVERSE_SIZE]
        buf[519] = ord('T')
        buf[520] = ord('K')

        self.code_num = (self.code_num + 1) % 256
        encoded = encryption1(buf, 5, DMX_UNIVERSE_SIZE, self.code_num, 517)

        self.ser.write(encoded)
        self.ser.flush()

    def close(self):
        self.ser.close()


def main():
    port = sys.argv[1] if len(sys.argv) > 1 else "COM6"
    print(f"Ouverture de {port} ...")
    link = DmxLink(port)

    print("Envoi du handshake de connexion ...")
    link.connect()

    try:
        print("Clignotement des canaux 1-43 (Lyre1 adr.1, Lyre2 adr.15, Spider adr.29).")
        print("Observe tes 3 appareils - Ctrl+C pour arreter.")
        on = False
        last_blink = 0.0
        while True:
            now = time.time()
            if now - last_blink >= 1.0:
                on = not on
                value = 255 if on else 0
                for ch in range(1, 44):
                    link.set_channel(ch, value)
                last_blink = now
                print(f"-> canaux 1-43 = {value}")

            link.send_frame()
            time.sleep(0.1)  # ~10 Hz, comme l'appli d'origine
    except KeyboardInterrupt:
        print("Arret demande.")
    finally:
        for ch in range(1, 44):
            link.set_channel(ch, 0)
        link.send_frame()
        time.sleep(0.1)
        link.disconnect()
        link.close()
        print("Deconnecte.")


if __name__ == "__main__":
    main()
