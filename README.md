# FlashBang

Multiplayer GPose for Final Fantasy XIV, built on [Brio](https://github.com/Etheirys/Brio).

Put lights, props and cameras in a **Sync** folder in the scene list, share a room code with a friend, and once they agree to sync, everything in that folder shows up in their GPose too, live.

> FlashBang is an early, unofficial project. It is not made or supported by the Brio team or the Lightless team. Please don't report FlashBang problems to them.

## Install

1. In game, open Dalamud settings (`/xlsettings`) → **Experimental**.
2. Under **Custom Plugin Repositories**, paste this link, tick it, and save:
   ```
   https://raw.githubusercontent.com/ambychannel/FlashBang/main/repo.json
   ```
3. Open the plugin installer (`/xlplugins`), find **FlashBang**, and install it.
4. Turn **Brio** off. FlashBang includes everything Brio does, and the two can't run together.

Open FlashBang with `/flashbang`.

## Status

| Milestone | What it adds | State |
| --- | --- | --- |
| 1 | Sync folder, room codes, lights | In progress |
| 2 | "Agree to sync" prompt, leave room, safety limits | Planned |
| 3 | Props, cameras, weather and time | Planned |

## Works with Lightless

FlashBang keeps Brio's IPC, so Lightless (including GPose Together) keeps working with it. Lightless still handles everyone's modded appearance; FlashBang handles the scene.

## Credits and license

FlashBang is a modified version of **Brio** by Minmoose, Asgard and contributors ([Etheirys/Brio](https://github.com/Etheirys/Brio)). The full history of the original project is kept in this repository. Brio's original README is in [BRIO_README.md](BRIO_README.md), and its acknowledgements are in [Acknowledgements.md](Acknowledgements.md).

FlashBang is licensed under the GNU General Public License v3.0, the same license as Brio. See [LICENSE](LICENSE).

Using third-party plugins is against the FINAL FANTASY XIV terms of service. Use at your own risk.
