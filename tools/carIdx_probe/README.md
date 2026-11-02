# carIdx_probe (W10 PiP onboard check)

Does iRacing send steering / gear / RPM for OTHER cars in a live session? The PiP virtual onboard needs it; .ibt files
don't carry CarIdx arrays, so it can only be checked live.

On the PC (iRacing running, in a live session - driving or spectating):

    cd tools\carIdx_probe
    dotnet run -c Release -- 120

It logs one line per second for 2 minutes and ends with a VERDICT line; the log is saved to
`Documents\iRacing\carIdx_probe.txt`. Grandstand's PiP already stands down by itself when the chasing car's data is all
zero or frozen for 2 s, and it never plays in a replay.
