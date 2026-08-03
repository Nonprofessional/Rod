# Regenerate the Go wire bindings from the teamserver proto.
#
# The generated *.pb.go files are committed to the tree (CI does not run protoc);
# rerun this by hand when src/Rod.Protocol/protos/rod.proto changes, then commit
# the result. Requires protoc plus the protoc-gen-go / protoc-gen-go-grpc plugins
# on PATH (go install google.golang.org/protobuf/cmd/protoc-gen-go@latest and
# google.golang.org/grpc/cmd/protoc-gen-go-grpc@latest).

PROTO := ../src/Rod.Protocol/protos/rod.proto

proto: $(PROTO)
	protoc \
	  --go_out=. --go_opt=paths=source_relative \
	  --go-grpc_out=. --go-grpc_opt=paths=source_relative \
	  -I $$(dirname $(PROTO)) $(PROTO)
