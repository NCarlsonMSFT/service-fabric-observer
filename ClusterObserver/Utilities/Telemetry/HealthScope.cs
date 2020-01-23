﻿// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace FabricClusterObserver.Utilities.Telemetry
{
    public enum HealthScope
    {
        Application,
        Cluster,
        Node,
        Partition,
        Replica,
        Service,
    }
}