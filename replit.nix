{ pkgs }: {
  deps = [ pkgs.curl pkgs.gnutar pkgs.gzip pkgs.bash pkgs.openssl pkgs.icu pkgs.zlib pkgs.stdenv.cc.cc.lib ];
  env = {
    LD_LIBRARY_PATH = pkgs.lib.makeLibraryPath [ pkgs.openssl pkgs.icu pkgs.zlib pkgs.stdenv.cc.cc.lib ];
  };
}
