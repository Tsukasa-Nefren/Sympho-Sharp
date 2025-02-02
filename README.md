# Sympho-Sharp
 **THIS PROJECT IS CURRENTLY IN ALPHA TEST IF YOU HAVE AN ISSUES RELATED TO ERROR PLEASE LEAVE AN ISSUES IN THIS REPO**
 
 Playing audio through voice chat instead of uploading sound file to workshop and precaching sound to emit in game. Which player don't have to update and download workshop with a ton of sound

 ## Feature
- Configure sound command with multiple sound choice.
- Playing Youtube audio with ``yt-dlp``
- Anti-Spamming option (Work In Progress).


## Requirement
- [AudioPlayer](https://github.com/samyycX/AudioPlayer)
- FFmpeg
- [yt-dlp](https://github.com/yt-dlp/yt-dlp)

## Installation
1. Drag all files into ``addons/counterstrikesharp/``
2. Install all requirement above.
3. Enjoy

## Command
- ``css_yt <url> [second_start]`` Playing youtube audio sound. (second_start is optional for setting which second of audio should start play)
- ``css_stopall`` Admin Command for stop all sound from playing in that moment.

## Example Sound Configuration

You can add list of the sound by edit ``Sympho/sounds/sounds.json`` file. Example usage for "ayaya" sound, if just ``!ayaya`` will random select sound in the list to be played, but if put the number behind command like ``!ayaya 2``, File "ayaya 2.mp3" will be played.

```jsonc
[
    {
        "name": [ "!tuturu", "!tutu" ], // they can have multiple command to play the same one sound.
        "sounds": [ "tuturu.wav" ]
    },
    {
        "name": [ "!oyl" ],
        "sounds": [ "oyl/grenade.mp3", "oyl/yabai.mp3" ] // you can add folder to organize your sound files.
    },
    {
        "name": [ "!ayaya" ],
        "sounds": [ "ayaya 1.mp3", "ayaya 2.mp3", "ayaya 3.mp3", "ayaayaya.mp3" ] // one command can have multiple sound.
    },
    {
        "name": [ "!rine" ],
        "sounds": [ "rine.mp3" ]
    }
]
```