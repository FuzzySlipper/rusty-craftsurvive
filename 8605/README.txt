rusty-craftsurvive #8605 (S9) - core-loop playtest without debug commands

Captured through the crew playtest lane (profile rusty-craftsurvive, a browser on the product
host at 127.0.0.1:37305, Engine pair 0.1.0-dev.8d76bac3ad3a, den-agents, RX 9070 XT), main at
130e86b plus the data-recipe tag on craft buttons (committed with this evidence).

Every game action is player input delivered by `playtest`: keys (WASD, Shift, Space, J, R, 1-9,
I, M, Escape), mouse look under pointer lock, and clicks on the product's own UI (craft buttons,
the Menu's Enter and Leave). The driver (driver/) reads the Engine's read-only playtest.observe
and product readouts to steer and to log outcomes; it runs no craft.* command that changes the
game.

core-loop.mp4        the lane's 68 original frames, in capture order, at 2 fps (a stop-motion
                     of the run, one frame per input step; not a continuous video)
core-loop-sheet.png  the twelve milestone captures
driver-journal.txt   the driver's log, with each outcome as the product reported it

The loop: starving at night, eat a ration from the hotbar (R); craft torches in the pack; read
the journal; walk 55 m to a hostile and defeat it with the attack key (level 3; meat and a claw
picked up; health fell to 4/30); apply a bandage; walk to a dungeon entrance; Enter from the Menu
(loaded in 415 ms, so no loading screen was caught); look and walk inside; walk back to the way
out and Leave; place a torch from the hotbar (the first try refused: the aim overlapped the
player); craft a ration from the fight's meat; eat.

Not shown: Rest (the world clock had reached day). The first session was replaced after a
failed mouse move degraded it (recover); its frames are included.
