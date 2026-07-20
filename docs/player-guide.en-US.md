## Guide

### 0. Building Lines

Place the depot as close to the origin station as possible. ATO needs to observe the travel time from the depot to the origin station to keep departures on time. If the depot is too far away, uncertainty along the access route may increase, making first departures or replacement vehicles less stable.

Lines built with bidirectional single-track railway sections are currently not supported. Depot access tracks and station throat branches are exceptions. When passing through a station, make sure the track actually goes through the station instead of bypassing it. You can refer to the images for recommended layouts.

### 1. Interfaces

ATO has two in-game interfaces: the **Workbench panel** and the **Selection panel**.

Supported languages: English, Simplified Chinese, and Japanese.

Left-click the mod button in the upper-right corner to open the Workbench panel. The Workbench is used to configure Departure Control, Broadcast, Passenger Flow, and Overview settings. The Overview page can switch between Train and Subway. This only affects the transport type currently being edited in the Workbench, and does not stop the other transport type from running; Train and Subway can both be controlled by ATO at the same time.

Right-click the mod button in the upper-right corner to open the Selection panel. The Selection panel shows runtime information for the selected vehicle, line, or station, and can also mark a selected station as a Bypass Station.

### 2. Timetables

ATO currently supports exact departures from the **origin station**. Station names are taken from the station building name.

On the right side of the Departure Control page, you can add departure times through Automatic or Manual methods. The automatically generated **Trips Per Hour** value supports decimals, but departures from the same origin station are subject to a 5-minute minimum gap.

Game time is different from real-world time. As a reference, a roughly 2 km interstation section may already take more than ten in-game minutes of pure running time. Do not interpret in-game timetables purely by real-world metro frequency. For initial setup, start with a lower frequency, such as 1–2 trips per in-game hour per line.

You can manually select a depot for each line. For lines that may connect to multiple depots, it is recommended to specify a single depot.

Only lines with an applied timetable are controlled by ATO. Lines without an applied timetable continue to use vanilla behavior.

**Max Origin Wait**: The maximum time a vehicle may arrive early and wait at the origin station. Default is 20 minutes, with a configurable range of 5–120 minutes. If a vehicle completes a full run and returns to the origin while the next departure is within this limit, it will usually wait at the origin for the next trip; if the wait is too long, it may return to depot.

**Max Dwell**: The maximum time a vehicle may stay at intermediate stations. Default is 10 minutes, with a configurable range of 5–120 minutes. When this limit is reached, the vehicle will be forced to depart. This limit does not apply at the origin station.

### 3. Broadcast System

The Broadcast page can import `.wav`, `.mp3`, and `.ogg` audio files. Imported audio files are copied to the ModsData folder under the game user directory and stored separately for Train and Subway.

Vehicle broadcasts currently support these trigger conditions: Approaching Station, Start Boarding, Leaving Station, and En Route Trigger. Platform Broadcast supports: Approaching Station.

Broadcast rules can be built from audio clips, pause nodes, and dynamic variables. Dynamic variables include Current Station, Next Station, Terminal Station, and Forward Turnback, which refers to the next turnback point ahead.

On the Station Audio Binding page, Auto Bind matches audio file names with station names. File names may use spaces, underscores, or hyphens. For example, if the station is named Central, `Central.wav`, `Central_zh.wav`, and `Central-en.mp3` will all be treated as candidates. If a station has exactly one candidate, it will be bound automatically. If multiple candidates are found, they remain as conflict items so you can manually specify the language order.

The binding order for each station corresponds to the language order used by broadcast variables. For example, if the first bound audio is a Chinese station name and the second is an English station name, the “Next Station” variable using language slot 1 will play the Chinese station-name audio, while slot 2 will play the English station-name audio.

For stability, clearing assets in the Workbench only removes ATO’s asset records. It does not delete the original audio files from disk.

### 4. Rapid/Local Bypass

First configure Rapid and Local services in the Workbench, then select a specific station in the Selection panel and mark it as a Bypass Station.

A Bypass Station should have a track layout that allows the Local service to wait while the Rapid service passes. A station with only one main track and no passing space should not be marked as a Bypass Station.

Bypass decisions are based on catch-up risk on shared sections: when a Rapid service may catch a Local service before the next Bypass Station, the Local service may wait at the Bypass Station until the Rapid service passes or the risk is cleared.

After enabling Rapid/Local bypass operation, it is recommended to test with only a few lines first.

### 5. Passenger Flow

The Passenger Flow page records real data in an approximately 24-hour in-game rolling window, including station boardings/alightings, section loads, OD matrix data, and line-filtered views.

This data comes from actual passenger changes during vehicle operation, not merely from citizen navigation data. After loading a save or enabling a line for the first time, the data needs time to accumulate. If a line was recently rebuilt, station names were changed, or vehicles have not completed a full run yet, the Passenger Flow page may temporarily be empty or incomplete.
