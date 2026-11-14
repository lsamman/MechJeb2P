# MechJeb2P

**MechJeb 2P** ("P" for precision) is a fork of [MechJeb2](https://github.com/MuMech/MechJeb2) for landing boosters and
ships precisely enough to be caught by a tower, as the Starship Expansion Project's Superheavy and Starship are. It adds to
the Landing Guidance window, under two collapsible sections, and leaves every other part of MechJeb as upstream has it.

## What 2P adds

**Precision catch (2P)**

- *Boostback*: in precision mode the standard course correction is replaced by a boostback step. A booster coming off its
  ship turns around under a small throttle (the gimbals only have torque with thrust on; a booster has no RCS), opens the
  throttle as the aim closes, and burns until the predicted landing site is within a set distance of the target. It only
  stands down when the air gets thick (a setting), where MechJeb's own step gives up as soon as any drag appears.
- *Precision terminal guidance*: the standard final descent is replaced, below a chosen height above the catch point and
  once the braking still to do fits the engines, by a closed-loop controller that flies the vessel's lowest point onto the
  target coordinates at a *catch altitude*, slows to a *capture speed*, holds position there (hover) for a time, then sinks
  at the capture speed until something holds the vessel up. KSP only calls a vessel "landed" on terrain, so the catch is
  detected as the vessel no longer sinking while asked to: the engines then go off, and are relit if it falls again.
  Translation uses a limited thrust tilt (more to brake an incoming speed) plus RCS.
- *Engine mode timing*: the braking phase's engine mode is selected when the coast to the braking burn begins, so the burn
  is timed for the engines it will have; the terminal phase's mode (fewer engines) only once the remaining braking fits the
  thrust that will be left (*Thrust left after terminal engine switch*).
- *Catch point = here*: on the pad, one button takes the target coordinates, the catch altitude and the catch heading from
  the vessel.
- *Hold heading*: the vessel's top is kept on a compass heading during the terminal descent, so the catch pins face the arms.
- *Aero steering*: during the atmospheric entry the vessel holds an angle of attack (up to a limit) toward the target, so
  body lift moves the predicted landing site. For vehicles entering engines-first.
- *Braking burn max tilt*: the tilt the braking burn may use to steer the landing site is a setting (upstream: 5.7°).
- *Keep flying when not focused*: switch to the tower, open the chopsticks, and the landing goes on. While a landing with
  this option runs, the vessel, its target and the grounded vessels near the target (the tower) keep load ranges of 150 km
  (configurable) instead of KSP's 2.25 km, and the flying vessel is never put on rails while loaded. A rails time warp
  started from the other vessel is dropped, and so is a physics warp during the boostback burn or the precision descent.
  A small **MechJeb2P remote landings** window shows the unfocused vessel's status with Abort and Switch-to buttons, even
  on a vessel without MechJeb.
- *Flight log*: each landing writes `Logs/MechJeb2P/<date>_<vessel>.csv` in the KSP folder, with the settings in force, a
  telemetry row several times a second (every physics tick during the boostback burn and the precision descent) and an
  event row for each step change, part option change, focus change and the reason the landing ended. Events also go to
  `KSP.log` with a `[MechJeb2P]` prefix.
- *SEP catch preset* fills all of the above (and the part options below) for a Starship Expansion Project catch;
  *Stock behaviour* turns 2P off again.

**Part control (2P)**

For each landing phase (entry, braking burn, terminal descent) the autopilot can set part options the way you would click
them in the part windows: control surfaces' pitch / yaw / roll authority and deployment (grid fins, flaps, including the
Starship Expansion Project's own control surfaces), engine gimbal pitch / yaw / roll and lock, RCS, SAS, a custom action
group fired once, and an engine mode (the engineID of a SEP cluster mode such as `CenterThree`, or a stock multi-mode
engine). SEP's cluster modes are cumulative: `OuterTwenty` runs all 33 engines, `MiddleEight` the inner 13, `MiddleTwo`
the inner 5, `CenterThree` the centre 3. The options the parts had before the landing are restored when it ends.

## Using it for a tower catch

1. On the pad, with the booster in the arms: Landing Guidance → Precision catch (2P) → **SEP catch preset**, then
   **Catch point = here**. Check the engine modes per phase under Part control (the preset sets the BL-3 booster's).
2. Fly the ascent and separation as usual. On the booster, with the catch point as the target, press **Land at target**
   as soon as it is free of the ship: the boostback starts at once.
3. Switch to the tower (`[` / `]`), work the chopsticks, and watch. The remote window shows the booster's status.
4. The booster hovers at the catch altitude, then sinks until the arms hold it and the engines go off.
5. If anything went wrong, the flight log in `Logs/MechJeb2P/` says what the autopilot saw and did.

## Building

The .NET SDK (8 or later) and a KSP install. Put your paths in `Directory.Build.props.user` (see
`Directory.Build.props`), then `./deploy.sh` builds `MechJeb2` and `MechJeb2.UI` in Release and copies both into
`GameData/MechJeb2/Plugins` of that install. `MechJeb2.UI.dll` draws every MechJeb window and must be present.

Upstream's README follows.

---

# MechJeb2

Anatid Robotics and Multiversal Mechatronics proudly presents the first flight assistant autopilot: MechJeb

MechJeb2 is a mod for the game Kerbal Space Program. To learn how to use it, [visit the wiki][wiki]. For more
info, [visit this KSP forum post][post].

[wiki]: https://github.com/MuMech/MechJeb2/wiki

[post]: http://forum.kerbalspaceprogram.com/index.php?/topic/154834-122-anatid-robotics-mumech-mechjeb-autopilot-260-12-dec-2016/

## Table of Contents

- [MechJeb2](#mechjeb2)
    - [Table of Contents](#table-of-contents)
    - [Install](#install)
        - [Manual install](#manual-install)
            - [Download](#download)
            - [Unpack](#unpack)
        - [Via CKAN](#via-ckan)
            - [Development version of Mechjeb](#development-version-of-mechjeb)
    - [Common Issues](#common-issues)
    - [Development](#development)
        - [Maintainers](#maintainers)
        - [Code Standards](#code-standards)
        - [Third-party libraries](#third-party-libraries)
        - [Build](#build)
            - [Linux](#linux)
            - [Windows](#windows)
    - [License](#license)

## Install

### Manual install

#### Download

Download from Jenkins:
<https://ksp.sarbian.com/jenkins/job/MechJeb2-Release/>

#### Unpack

Unzip the zip in KSP GameData directory. You should have something that looks like that :

    Kerbal Space Program
    -- GameData
       -- MechJeb2
          -- Bundles
          -- Icons
          -- Localization
          -- Parts
          -- Plugins

### Via CKAN

CKAN has all the release of MechJeb, just install it as usual.

#### Development version of Mechjeb

If you want the unstable dev version of MechJeb then :

1. Open CKAN settings (Settings => CKAN Settings)
2. Press the New button
3. Select the MechJeb-dev line, click OK and exit the options.
4. Refresh
5. Select "Mechjeb2 - DEV RELEASE" in the list
6. Then "Go to Change" to install

## Common Issues

1. Why is the Mechjeb menu not showing?

   Make sure you have the part on your ship (AR202 case in the Control section).

2. (Windows) I cannot find Mechjeb anywhere, there aren't even parts in the R&D facility!

   Some Windows protection and anti-virus software can sometimes block KSP from loading MechJeb.
   You should install KSP outside the `C:\Program Files (x86)\`
   directory. [Steam has an option to change the install directory](https://support.steampowered.com/kb_article.php?ref=7710-tdlc-0426)
   of a game or you can just copy the directory somewhere else.

3. Why is some Mechjeb function not available?

   Science and career mode requires you to unlock some specific node in the Research and Development tree.
   You also may need to upgrade the tracking station to level 2 (game code restriction we can't do much about).

4. How do I report a bug?

   Check if your problem has already been reported: <https://github.com/MuMech/MechJeb2/issues>  
   If you found a problem which is similar to yours, feel free to add more information to the existing issue.

   **If you cannot find the problem**, get
   a [log](https://forum.kerbalspaceprogram.com/index.php?/topic/83212-how-to-get-support-read-first/#Logs) and create a
   new issue with a descriptive title of the problem.

## Development

### Maintainers

- [@sarbian](https://github.com/sarbian)
- [@lamont-granquist](https://github.com/lamont-granquist)

### Code Standards

1. [No var](https://docs.microsoft.com/en-us/visualstudio/ide/reference/convert-var-to-explicit-type): use explicit
   types.
2. Prefer single lines, when possible: especially if-else blocks!
3. [No null-conditional operators](https://docs.microsoft.com/en-us/dotnet/csharp/language-reference/operators/member-access-operators#null-conditional-operators--and-):
   Unity 4.x has
   a [custom == for checking object nulls](https://blog.unity.com/technology/custom-operator-should-we-keep-it).
4. Assembly version needs to remain at 2.5.1.0; file version can be incremented.

### Third-party libraries

- [ALGLIB](https://www.alglib.net/)
- [NSubstitute](https://nsubstitute.github.io/)
- [xunit](https://xunit.net/)

### Build

#### Linux

The project uses Mono and Make to build the addon, make sure you have both installed. You need Nuget to download the external dependencies.

You can also use the [flake.nix](./flake.nix) with direnv or by runnning `nix develop` to set up the development environment.

1. (optional) Set your KSP directory

```sh
export KSPDIR="${XDG_DATA_HOME}/Steam/SteamApps/common/Kerbal Space Program"
```

2. Fetch external packages

```
nuget restore
```

3. Build the mod: `make build` builds the five assemblies that go in `GameData/MechJeb2/Plugins`, the UI (`MechJeb2.UI.dll`) included

```sh
make build
```

The .NET SDK builds it too, as CI does: `dotnet build MechJeb2.sln -c Release -p:KspDir="$KSPDIR"`.

4. (optional) Install the mod into your KSP directory

```sh
make install
```

#### Windows

1. Install the version of Unity that KSP uses ( Currently 2019.2.2f1 )

2. Configure your system environment variables and add:

- KSPDIR set to where your KSP install is ( usually **C:\Program Files (x86)\Steam\SteamApps\Common\Kerbal Space Program
  ** )
- MONO set to the path of Unity current mono.exe ( usually C:\Program
  Files\Unity\Hub\Editor\2019.2.2f1\Editor\Data\MonoBleedingEdge\bin\mono.exe )
- PDB2MDB set to the path of pdb2mdb.exe ( usually **C:\Program
  Files\Unity\Hub\Editor\2019.2.2f1\Editor\Data\MonoBleedingEdge\lib\mono\4.5\pdb2mdb.exe** )

3. Load MechJeb2.sln and open the properties of the MechJeb2 project (Right-Click=>properties). In the "Reference Path"
   section add the KSP libs folder to the list ( usually **C:\Program Files (x86)\Steam\SteamApps\Common\Kerbal Space
   Program\KSP_x64_Data\Managed** )

4. Repeat step 3 for the MechJebLib, MechJebLibBindings, and MechJebLibTest projects.

5. Perform `nuget restore` to get external dependencies such as JetBrains.Annotations.

## License

Licensed under the [GNU General Public License, Version 3](LICENSE.md).

Portions (in the "MechJebLib" directory) are placed in the public domain and are documented in
the affected source code headers.
