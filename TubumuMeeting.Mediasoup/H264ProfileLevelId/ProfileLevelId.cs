﻿using System;
using System.Collections.Generic;
using System.Text;

namespace TubumuMeeting.Mediasoup
{
    public class ProfileLevelId
    {
        public int Profile { get; set; }

        public int Level { get; set; }

        public ProfileLevelId(int profile, int level)
        {
            Profile = profile;
            Level = level;
        }
    }
}