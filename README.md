# Sympho-Sharp
 **THIS PROJECT IS CURRENTLY IN ALPHA TEST IF YOU HAVE AN ISSUES RELATED TO ERROR PLEASE LEAVE AN ISSUES IN THIS REPO**
 
 Playing audio through voice chat instead of uploading sound file to workshop and precaching sound to emit in game. Which player don't have to update and download workshop with a ton of sound

 ## Feature
- Configure sound command with multiple sound choice.
- Playing Youtube audio with ``yt-dlp``
- Anti-Spamming option (Work In Progress).


## Requirement
- [AudioPlayer](https://github.com/samyycX/AudioPlayer)
- [FFMpeg](https://www.ffmpeg.org/) (This link is windows for linux use terminal command to install instead.)
- [yt-dlp](https://github.com/yt-dlp/yt-dlp)

## Installation
1. Drag all files into ``addons/counterstrikesharp/``
2. Download and place ``yt-dlp`` binariy file ***(Depending your os, if Windows it with be .exe)*** into Sympho plugin folder.
3. Set permission for both ``yt-dlp`` to 0764 or greater.
4. Enjoy

## Command
- ``css_yt <url> [second_start]`` Playing youtube audio sound. (second_start is optional for setting which second of audio should start play)
- ``css_stopall`` Admin Command for stop all sound from playing in that moment.