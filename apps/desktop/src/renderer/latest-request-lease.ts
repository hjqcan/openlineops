export class LatestRequestLease {
  private epoch = 0;
  private activeEpoch: number | null = null;

  get busy(): boolean {
    return this.activeEpoch !== null;
  }

  start(): number {
    const requestEpoch = ++this.epoch;
    this.activeEpoch = requestEpoch;
    return requestEpoch;
  }

  isCurrent(requestEpoch: number): boolean {
    return this.epoch === requestEpoch;
  }

  cancel(): boolean {
    this.epoch++;
    const wasBusy = this.busy;
    this.activeEpoch = null;
    return wasBusy;
  }

  finish(requestEpoch: number): boolean {
    if (this.activeEpoch !== requestEpoch) {
      return false;
    }
    this.activeEpoch = null;
    return true;
  }
}
