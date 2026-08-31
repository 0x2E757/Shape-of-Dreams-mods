namespace BuildWhileDown
{
    // The whole mod asks one question - "is the local player editing from the floor?" - and
    // answers it in one place so that the four patches cannot drift apart.
    //
    // The two methods below that take a manager are stand-ins: the transpilers in EditWhileDown.cs
    // replace a property read inside a named method with a call to one of these, and nothing else.
    // That is the entire reason they are shaped like properties with the instance passed in - the
    // substitution has to leave the evaluation stack exactly as it found it.
    //
    // A stand-in rather than a patch on the property itself, because both properties have callers
    // that must go on getting the truth. shouldProcessCharacterInput is what stops a knocked-out
    // hero casting and moving, and isSpectating is what points the camera at somebody else.
    internal static class Down
    {
        // The one question. Deliberately narrow: this is the *local* player's *own* hero, so a
        // spectated teammate's state never enters into it.
        public static bool Editing()
        {
            if (!BuildWhileDownMod.IsLive) return false;

            var player = DewPlayer.local;
            if (player == null) return false;

            var hero = player.hero;
            return hero != null && hero.isKnockedOut;
        }

        // Stands in for ControlManager.shouldProcessCharacterInput.
        //
        // The game computes that property as shouldProcessCharacterInputAllowKnockedOut with the
        // knocked-out case taken back out again:
        //
        //     shouldProcessCharacterInput = shouldProcessCharacterInputAllowKnockedOut
        //         && (!(controllingEntity is Hero hero) || !hero.isKnockedOut);
        //
        // So this is not a way of saying yes to everything - it is the same expression without
        // that last clause, which is precisely the clause the mod exists to drop. Everything else
        // that closes character input - a cutscene, the world map, a message on screen - still
        // closes it here.
        public static bool ProcessInput(ControlManager control)
        {
            if (control == null) return false;
            if (control.shouldProcessCharacterInput) return true;
            return control.shouldProcessCharacterInputAllowKnockedOut && Editing();
        }

        // Stands in for CameraManager.isSpectating.
        //
        // Down in co-op, the camera moves to a living teammate after a couple of seconds, and
        // three separate places read that to mean "this player is a bystander now": the edit
        // manager refuses to open, the edit manager closes what is open, and the bottom bar fades
        // itself to nothing. The camera is not what the mod is arguing with - it should follow the
        // teammate, and it still does - so the lie is told only to those three.
        public static bool Spectating(CameraManager camera)
        {
            if (camera == null) return false;
            if (!camera.isSpectating) return false;

            return !Editing();
        }
    }
}
