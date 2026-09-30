# Architecture

Generate registered transparent PNG layers from `Avatar.jpg` using a deterministic local build tool. Render the layers in the source image coordinate system as a 2D raster puppet: legs, torso, arms, hands, and head share the original illustration and anatomical pivots.

The existing deterministic movement state, keyboard controls, bubble timing, clap timing, and loopback server remain. The renderer and CSS are replaced so no old SVG body geometry, photographic face, joint circles, oval waist coupling, or helmet-like frame remains.

The private source image is never served. Only metadata-free generated PNG layers are available at runtime.
