﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TeslaSQL
{
    class Notifier : Agent
    {
        public override void ValidateConfig()
        {
            Config.ValidateRequiredHost(Config.relayServer);
            if (Config.relayType == null) {
                throw new Exception("Notifier agent requires a valid SQL flavor for relay");
            }
        }

        public override int Run()
        {
            throw new NotImplementedException();
        }
    }
}