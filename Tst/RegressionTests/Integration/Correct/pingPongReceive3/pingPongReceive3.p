event ping0: machine;
event ping1: machine;
event pong0;
event pong1;

machine Main {
  var Follower0: machine;
  var Follower1: machine;
  var count: int;

  start state Init {
    entry {
      var id: int;
      Follower0 = new Follower();
      Follower1 = new Follower();
      id = 1;
      while(id <= 2) {
        send Follower1, ping0, this;
        print format ("{0}: ping0 sent", id);
        receive {
            case pong0: {
                count = (count + 1) * id;
                print format ("{0}: pong0 received", id);
           }
        }
        count = (count + 1) * id;
        print format ("{0}: done", id);
        id = id + 1;
      }
      assert(count == 14), format ("count = {0}", count);
    }
  }
}

machine Follower {
  start state Init {
    on ping0 do (sender: machine) {
      send sender, pong0;
    }
    on ping1 do (sender: machine) {
      send sender, pong1;
    }
  }
}
