﻿using System;
using System.Collections.Generic;
using System.Text;

namespace TubumuMeeting.Mediasoup
{
    public class NoyifyMessage
    {
        public ArraySegment<byte> Message { get; set; }

        public int PPID { get; set; }
    }
}
