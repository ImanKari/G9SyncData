// Removes the build output of the package npm runs this for (its working directory), so a rebuild never ships stale
// files. Shared by the workspace's packages (each runs it from its own folder).
import { rm } from 'node:fs/promises';
import { join } from 'node:path';

await rm(join(process.cwd(), 'dist'), { recursive: true, force: true });
